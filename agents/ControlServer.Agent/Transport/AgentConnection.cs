using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace ControlServer.Agent.Transport;

/// <summary>
/// 客户端到 <c>/hubs/agent</c> 的长连接（M4.1）。
/// <para>
/// <b>凭据走请求头而不是 URL 查询串</b>：查询串会被服务端的请求日志原样记下来
/// （<c>UseSerilogRequestLogging</c> 会打 <c>RequestPath</c>），一次握手就等于把 agentKey
/// 写进了服务器的日志文件。头不会被记。服务端两种都支持（头优先），
/// 这里只走头这一条路。
/// </para>
/// <para>
/// <b>不使用 SignalR 的自动重连</b>：重连策略要和外层"重新发现 / 重新注册"的节奏对齐，
/// 两套退避叠在一起会出现"重连在跑、注册也在跑"的竞态。这里只做一次连接，
/// 断了就抛给 <c>AgentRuntime</c> 统一处理 —— 一处退避，一处状态。
/// </para>
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public AgentConnection(string serverUrl, string agentKey, LogService log)
    {
        var hubUrl = BuildHubUrl(serverUrl);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.Headers[AgentAuthentication.HeaderName] = agentKey;

                // 内网常常是 http；服务端重定向到 https 时不要让握手在 302 上丢头。
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets
                    | Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        _connection.On<AgentWelcomeDto>(AgentHubMethods.Welcome, welcome =>
        {
            WelcomeReceived?.Invoke(welcome);
            return Task.CompletedTask;
        });

        _connection.On<AgentCommandDto>(AgentHubMethods.Command, command =>
        {
            CommandReceived?.Invoke(command);
            return Task.CompletedTask;
        });

        _connection.On<string>(AgentHubMethods.Blocked, reason =>
        {
            Blocked?.Invoke(reason);
            return Task.CompletedTask;
        });

        // ── 文件传输任务派发（M4.4）──
        // ⚠️ 方法名用常量而不是字面量：写错一个字母只会表现为"服务端说发了、这边毫无反应"，
        //    **没有任何编译错误**（见 AgentHubMethods 的注释）。
        // 这里只入队、不搬字节：真正的传输由 TransferWorker 在**进程生命周期**上跑，
        // 免得一次 SignalR 抖动把正在传的大文件掐掉。
        _connection.On<AgentTransferTaskDto>(AgentHubMethods.PushTransferTask, task =>
        {
            TransferTaskReceived?.Invoke(task);
            return Task.CompletedTask;
        });

        // ── 配置已变更（M4.5）──
        // 载荷只有版本号：正文由客户端自己去 GET /api/agents/{id}/config 拉。
        // 这样 Hub 消息不会随配置变大而变大，而且"推送丢了"与"上线补拉"共用同一条取数路径。
        _connection.On<AgentConfigChangedDto>(AgentHubMethods.ConfigChanged, changed =>
        {
            ConfigChanged?.Invoke(changed);
            return Task.CompletedTask;
        });

        // ── 目录浏览（M4.4+，回传源路径选择）──
        // 🔥 与上面几条**方向相同、形状不同**：这是一次"一问一答" ——
        //    服务端 InvokeAsync 之后在等这边的**返回值**。所以处理器必须**同步返回结果**，
        //    不能像别的回调那样"入队后立刻返回"（那会让服务端拿到 null，表现为"浏览永远失败"）。
        //
        //     它本身很轻（列一个目录），没有理由为它引入一条队列 ——
        //     但要保证"绝不做长活"：一旦这里慢下来，拖住的是这条连接上的所有调用。
        _connection.On<AgentBrowseRequestDto, AgentBrowseResultDto>(
            AgentHubMethods.ListDirectory,
            request => DirectoryBrowser?.Invoke(request)
                ?? AgentBrowseResultDto.Unsupported("本客户端未启用目录浏览（文件传输已停用或能力未装配）。"));

        // ── 「接收到的数据」（2026-10-13，排查"指令不生效"）──
        // 与目录浏览同形：服务端在 InvokeAsync 上等返回值。
        // 它比浏览更轻（只是把内存里那个环形缓冲拷一份），所以同步返回是对的。
        _connection.On<AgentReceivedDataDto>(
            AgentHubMethods.ListReceivedData,
            () => ReceivedDataProvider?.Invoke()
                ?? AgentReceivedDataDto.Unsupported("本客户端版本过旧（不支持回看接收记录），请升级客户端后再试。"));

        // ── 输入回传（M5.2）──
        //
        // 🔥 <b>它是本类第一条"中心把事件推下来、由本机立刻照做"的消息</b>，
        //    形状与目录浏览一致（服务端在 InvokeAsync 上等返回值）——
        //    但理由完全不同：这里的返回值不是"查到了什么"，而是
        //    <b>"我收下了没有、没收到是为什么"</b>。
        //
        //    同步返回是必须的，而且**这里绝不能等**：输入是高频路径
        //    （拖动鼠标时每秒几十批），一旦在这个处理器里做任何耗时的事，
        //    拖住的就不只是输入 —— 这条连接上还挂着心跳、遥测、推帧。
        //    真正的活儿（转进托盘那条管道）是一次 <c>TryWrite</c>，非阻塞。
        _connection.On<RemoteInputBatchDto, AgentIngestAckDto>(
            AgentHubMethods.RemoteInput,
            batch => InputReceiver?.Invoke(batch)
                ?? AgentIngestAckDto.Rejected(
                    AgentInputStates.Unavailable,
                    "本客户端未启用远程输入（客户端版本可能过旧，或本机没有可注入的桌面）。"));

        _connection.Closed += error =>
        {
            log.Info("Agent", error is null
                ? "与中心服务端的连接已关闭。"
                : $"与中心服务端的连接已断开：{error.Message}");

            return Task.CompletedTask;
        };
    }

    /// <summary>服务端发来的第一条消息（含下发的心跳间隔与显示名）。</summary>
    public event Action<AgentWelcomeDto>? WelcomeReceived;

    /// <summary>服务端下发的指令（重启等）。</summary>
    public event Action<AgentCommandDto>? CommandReceived;

    /// <summary>被管理员吊销。</summary>
    /// <summary>被管理员**拉黑**（2026-10-15，取代原「吊销」）。</summary>
    public event Action<string>? Blocked;

    /// <summary>服务端派发了一条文件传输任务（M4.4，下发或回传）。</summary>
    public event Action<AgentTransferTaskDto>? TransferTaskReceived;

    /// <summary>服务端通知本机配置已变更（M4.5，载荷只带版本号）。</summary>
    public event Action<AgentConfigChangedDto>? ConfigChanged;

    /// <summary>
    /// 服务端请求列出本机的一个目录（M4.4+，回传源路径选择）。
    /// <para>
    /// <b>为什么是回调而不是事件</b>：服务端在 <c>InvokeAsync</c> 上等着返回值，
    /// 事件那种"发了就算"的形状给不出答复。为 null = 本机没装配这个能力
    /// （未启用文件传输时就是如此），此时回一个明确的"不支持"而不是抛异常 ——
    /// 抛出去在服务端会变成一个没有信息量的"调用失败"。
    /// </para>
    /// <para><b>实现方必须短</b>：它跑在 Hub 的调度上，慢下来会拖住这条连接上的其它消息。</para>
    /// </summary>
    public Func<AgentBrowseRequestDto, AgentBrowseResultDto>? DirectoryBrowser { get; set; }

    /// <summary>
    /// 服务端要一份「接收到的数据」快照（2026-10-13）。
    /// <para>
    /// <b>为 null = 本机答不了</b>（理论上不会：缓冲是 AgentRuntime 构造必需的依赖），
    /// 这时回一个明确的"不支持"而不是抛 —— 抛出去在服务端只是一个没有信息量的"调用失败"。
    /// </para>
    /// <para><b>实现方必须短</b>：跑在 Hub 调度上，慢下来会拖住这条连接上的其它消息。</para>
    /// </summary>
    public Func<AgentReceivedDataDto>? ReceivedDataProvider { get; set; }

    /// <summary>
    /// 中心<b>送回一批输入事件</b>（M5.2）。
    ///
    /// <para>
    /// 🔥 <b>它是本类唯一一条"中心推下来、要本机立刻作用到桌面上"的消息</b>，
    /// 所以它是一个<b>同步</b>回调而不是事件：服务端的 <c>InvokeAsync</c>
    /// 在等这句答复，而那句答复（"收下了 / 为什么没收"）是页面上
    /// 唯一能解释"点了没反应"的东西。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>实现方必须极短且绝不抛</b>：它跑在 Hub 的调度上，
    /// 而同一条连接上还挂着心跳、遥测与码流 —— 在这里慢一拍，
    /// 影响的是<b>整个遥控体验</b>，不只是输入。
    /// </para>
    /// <para>为 null = 本机没装配远程输入（非 Windows / 能力未启用），此时回明确的"不支持"。</para>
    /// </summary>
    public Func<RemoteInputBatchDto, AgentIngestAckDto>? InputReceiver { get; set; }

    /// <summary>
    /// <b>中心说"这一帧我拼不起来了"</b>时被调用一次，参数是会话 id（M5.4-c① 步 5）。
    ///
    /// <para>
    /// 🔴 <b>为什么需要这条反向信号</b>：H.264 的 P 帧参考前面的帧，<b>丢一帧就等于
    /// 在参考链上戳一个洞</b> —— 观看端从那一刻起每帧都是花的，<b>而解码器不会报错</b>。
    /// 被控端<b>自己丢帧时知道</b>（托盘那个 <c>wantKeyframe</c>），但<b>中心丢帧它不知道</b>
    /// —— 它只是把每一片都发出去了，看起来一切正常。所以只能由对面告诉它。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>实现方要自己节流，而且必须是"按会话、按时间"的节流</b>：
    /// 中心持续丢帧时会<b>每帧都要</b>，而"每帧都要关键帧"等于把一路流变成
    /// <b>全程 IDR</b>（8K 的 IDR 正是最贵的东西）。节流位置在
    /// <c>RemoteSessionCoordinator</c>（收到的一侧）—— 中心不知道对面多久能补上一个。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>它跑在发帧的路径上</b>（每帧最多一次），所以实现方<b>不许</b>在这里做
    /// 阻塞等待：一次 IPC 往返会直接加到那一帧的延迟上。正确做法是"置个标志就走"
    /// （托盘那边正是 <c>Session.RequestKeyframe</c> 的 <c>Interlocked</c> 标志）。
    /// </para>
    /// </summary>
    public Action<string>? KeyframeRequested { get; set; }

    /// <summary>
    /// <b>单片上限</b>（分片那条路的"一片能装多少字节"）。默认 = 协议上限
    /// （<see cref="RemoteStreamLimits.MaxChunkPayloadBytes"/>）。
    ///
    /// <para>
    /// 🔥🔥 <b>它是一个诊断旋钮，不是产品能力</b> —— 唯一用途是让
    /// <b>任何一台机器</b>都能走一遍分片路：把阈值降到 64 KB，
    /// 2160×1440 的普通帧也会被切成 5 片。这正是端到端冒烟
    /// （<c>deploy/smoke/remote-e2e.py --chunk-threshold</c>）证明
    /// "被控端真的切了 / 中心真的拼了 / 浏览器真的画了"的唯一办法 ——
    /// 否则要等一台 8K 显示器才能碰到这条路径（本机只有 2160×1440）。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>它只许调小、不许调大</b>：调大不会"更省"，它会让中心那条 SignalR 消息
    /// 超出 <see cref="RemoteStreamLimits.SignalRMessageBytes"/>，
    /// 而后果是<b>掐断整条连接</b>（心跳 / 遥测 / 配置 / 音频全在上面）。
    /// ⇒ 设置方（<c>AgentRuntime</c>）必须夹在
    /// <c>1 .. MaxChunkPayloadBytes</c> 之内，本类<b>不在发帧路径上二次校验</b>
    /// （那是每秒几十次的热路径，而输入只在启动那一刻来一次）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 调小还有第二道边界：片数 = <c>ceil(帧字节 / 阈值)</c>，
    /// 而中心只接受 ≤ <see cref="RemoteStreamLimits.MaxChunkCount"/> 片。
    /// 用 64 KB 时 <c>64 KiB × 1024 = 64 MiB</c> 正好等于整帧上限
    /// （<see cref="RemoteStreamLimits.MaxFrameBytes"/>）⇒ 边界刚好闭合。
    /// 再往下（比如 16 KB）理论上会让 64 MiB 的帧算出 4096 片 ⇒ <c>Slice</c> 抛。
    /// </para>
    /// </summary>
    public int ChunkThresholdBytes { get; set; } = RemoteStreamLimits.MaxChunkPayloadBytes;

    /// <summary>
    /// 读"降低分片阈值"那个环境变量的<b>名字</b>（诊断旋钮的入口）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 为什么走环境变量而不是 <c>appsettings</c> / 命令行：
    /// 它<b>不该</b>出现在客户机的配置文件里（那会让它看起来像一个可配置的产品能力，
    /// 然后有人把它设成 1 MB 再抱怨 CPU 高）。环境变量有三个好处：
    /// 不进配置快照、不进客户机 UI、<b>随进程生命周期结束自动消失</b> ——
    /// 与"这次诊断"这个语义完全对齐。
    /// </remarks>
    internal const string ChunkThresholdEnvVar = "CONTROLSERVER_CHUNK_THRESHOLD_BYTES";

    /// <summary>
    /// 把环境变量的原始字符串解析成"可用的单片上限"（+ 一句该不该说给人听的话）。
    /// </summary>
    /// <param name="raw">环境变量的原始值；<c>null</c>/空白 = 没设（正常情况）。</param>
    /// <returns>
    /// <c>Threshold</c>：可直接用的片上限（非法值一律回落到协议上限）；
    /// <c>Warning</c>：<c>null</c> = 一切正常；非 null = <b>必须打进日志</b>的一句话。
    /// </returns>
    /// <remarks>
    /// 🔥 <b>为什么非法值只警告、不抛</b>：这是个诊断旋钮，它的输入错误
    /// <b>不该让客户机起不来</b>（现场手抖多打一个 0 就整台机器离线，那太贵了）。
    /// 但<b>必须</b>说一句 —— 静默回落最坏的形状是"以为自己降了阈值，
    /// 结果跑了一整轮老路，然后拿这份结果去判断分片好不好"。
    /// </remarks>
    internal static (int Threshold, string? Warning) ResolveChunkThreshold(string? raw)
    {
        // 没设 = 生产常态（每一台客户机都是这个状态）⇒ 一个字的日志都不许有。
        if (string.IsNullOrWhiteSpace(raw)) return (RemoteStreamLimits.MaxChunkPayloadBytes, null);

        var text = raw.Trim();

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return (RemoteStreamLimits.MaxChunkPayloadBytes,
                $"{ChunkThresholdEnvVar}=\"{text}\" 不是整数 —— 分片阈值保持协议默认"
                + $"（{RemoteStreamLimits.Describe(RemoteStreamLimits.MaxChunkPayloadBytes)}）。"
                + "⚠️ 这一轮**没有**走诊断模式，帧仍然按默认阈值分片。");
        }

        if (value <= 0 || value > RemoteStreamLimits.MaxChunkPayloadBytes)
        {
            return (RemoteStreamLimits.MaxChunkPayloadBytes,
                $"{ChunkThresholdEnvVar}={value} 超出可用范围 1..{RemoteStreamLimits.MaxChunkPayloadBytes}"
                + $"（{RemoteStreamLimits.Describe(RemoteStreamLimits.MaxChunkPayloadBytes)}）—— 保持默认。"
                + "⚠️ 注意它**只许调小**：调大不会更省，它会让中心那条 SignalR 消息超出"
                + $" {RemoteStreamLimits.Describe(RemoteStreamLimits.SignalRMessageBytes)}"
                + "，后果是**掐断整条连接**（心跳 / 遥测 / 配置 / 音频全在它上面）。");
        }

        return (value, null);
    }

    /// <summary>当前连接状态（外层循环据此判断是否该重来）。</summary>
    public HubConnectionState State => _connection.State;

    /// <summary>建立连接。失败时抛（由外层统一退避重试）。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
        => _connection.StartAsync(cancellationToken);

    /// <summary>
    /// 主动断开（2026-10-15，被拉黑时用）。
    /// <para>
    /// 为什么由客户端自己断：服务端那边下一次心跳也会把它掐掉，但那要等一个心跳周期
    /// （默认 10 秒）；而"管理员刚点了拉黑"这件事没有理由再等 10 秒。
    /// </para>
    /// <para>正常路径不调它 —— 那个由 <see cref="DisposeAsync"/> 收尾。</para>
    /// </summary>
    public Task StopAsync() => _connection.StopAsync();

    /// <summary>
    /// 发一次心跳。
    /// <para>返回 null 表示这一跳没拿到应答（连接可能已经不可用）—— 不抛，让外层循环自然结束。</para>
    /// <para>
    /// 🔥 <b>参数个数必须保持 1，不许"顺手加个可选参数"</b>。实测（SignalR 8.0.10）：
    /// 客户端传 1 个实参而服务端方法有 2 个（哪怕第 2 个带 C# 默认值）⇒ <b>调用失败</b>；
    /// 反过来多传一个也一样。SignalR 要求实参个数与签名<b>严格一致</b>，
    /// 所以"给心跳挂上额外状态"这条路是死的 —— 那会让<b>旧客户端的心跳整条失败</b>
    /// （表现成几十台机器的在线状态一起变灰）。新增状态走
    /// <see cref="ReportUdpChannelAsync"/> 那条独立消息。
    /// </para>
    /// </summary>
    public async Task<AgentHeartbeatAckDto?> HeartbeatAsync(string agentVersion, CancellationToken cancellationToken)
    {
        try
        {
            return await _connection
                .InvokeAsync<AgentHeartbeatAckDto>(
                    AgentHubClientMethods.Heartbeat,
                    agentVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 上报本机 UDP 指令通道<b>此刻真实的</b>状态（2026-10-14）。
    /// <para>
    /// 🔥 <b>中心判"这台机器能不能收 UDP 指令"，不能只看"我下发过的配置里写着启用"</b>：
    /// 配置说启用不等于它在听（本机可能停用了任务下发、端口可能绑不上、配置可能还没应用）。
    /// 这条上报让中心拿到「客户机自己说的真相」，也是"点了没反应却两边都没错"的解法。
    /// </para>
    /// <para>
    /// ⚠️ <b>旧服务端不认识这个方法</b>（新客户端先升级的场景）：那时 <c>InvokeAsync</c> 会抛，
    /// 而这里统一吞掉并返回 null —— 与遥测上报同一条纪律。
    /// "这条能力暂不可用"绝不该升级成"断线重连"。
    /// </para>
    /// </summary>
    public Task<AgentIngestAckDto?> ReportUdpChannelAsync(
        AgentUdpChannelStateDto state,
        CancellationToken cancellationToken)
        => InvokeIngestAsync(AgentHubClientMethods.ReportUdpChannel, state, cancellationToken);

    // ───────────────────────────────────────────────────────────────────────
    // M4.2 遥测上报：三个方法都是"发一批，拿一个应答"。
    //
    // ⚠️ 三者的失败语义**完全不同**，别写成一样：
    //    · 指标：可以丢（5 秒后又有新的，且服务端按 (AgentId, At) 去重）⇒ 失败就原地重来；
    //    · 开关机事件：**绝不能丢**（一条记录没了就永远补不回来）⇒ 必须等服务端明确说 ok
    //      才允许推进本地水位（见 TelemetryCollector.CommitPowerEvents）；
    //    · 硬件清单：可丢（6 小时后再来），但"上报过"的时刻不能提前记，
    //      否则会变成"隔 6 小时才第一次上报"。
    //
    // 统一返回 null = "没拿到应答"（网络断 / 服务端拒收异常），调用方据此决定退不退水位。
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>上报一批实时指标（M4.2）。</summary>
    public Task<AgentIngestAckDto?> ReportMetricsAsync(
        AgentMetricsBatchDto batch,
        CancellationToken cancellationToken)
        => InvokeIngestAsync("ReportMetrics", batch, cancellationToken);

    /// <summary>上报一批开关机 / 异常事件（M4.2）。</summary>
    public Task<AgentIngestAckDto?> ReportPowerEventsAsync(
        AgentPowerEventBatchDto batch,
        CancellationToken cancellationToken)
        => InvokeIngestAsync("ReportPowerEvents", batch, cancellationToken);

    /// <summary>上报硬件清单（M4.2）。</summary>
    public Task<AgentIngestAckDto?> ReportInventoryAsync(
        AgentInventoryDto inventory,
        CancellationToken cancellationToken)
        => InvokeIngestAsync("ReportInventory", inventory, cancellationToken);

    /// <summary>
    /// 上报本机的视频硬编码能力（M5.0 第二批）。
    /// <para>
    /// 🔥 这份结论是 M5 整个 8K 目标的地基（编码格式选择、要不要开分帧编码都建在它上面），
    /// 而它<b>只能由客户机自己产生</b> —— 要编码的是被控端，中心有没有 NVENC 与它无关。
    /// </para>
    /// <para>
    /// ⚠️ <b>旧服务端不认识这个方法</b>：那时 <c>InvokeAsync</c> 会抛，
    /// 而这里统一吞掉并返回 null（与遥测上报同一条纪律）。
    /// "这条能力暂时用不上"绝不该升级成"断线重连"。
    /// </para>
    /// </summary>
    public Task<AgentIngestAckDto?> ReportEncoderCapabilityAsync(
        AgentEncoderCapabilityDto report,
        CancellationToken cancellationToken)
        => InvokeIngestAsync(AgentHubClientMethods.ReportEncoderCapability, report, cancellationToken);

    /// <summary>
    /// <b>回报本机有哪些屏</b>（M5.5 多屏选择）。
    ///
    /// <para>
    /// 🔥 <b>为什么必须新开一条上行，而不是搭「能力上报」的车：</b>
    /// 能力上报跑在<b>服务进程</b>里（Session 0），那里没有桌面、也没有显示器 ——
    /// DXGI 在 Session 0 枚举到的是 0 块屏。而"这台机器接了几块显示器"只有
    /// <b>托盘</b>（用户会话里那个进程）答得出来。所以这里是托盘 → 服务 → 中心
    /// 的一段单独上行，与 <c>ReportEncoderCapability</c> 只是长得像。
    /// </para>
    /// <para>
    /// ⚠️ <b>失败一律吞掉</b>（同能力上报）：没送到 ⇒ 中心那边停在
    /// "正在索取"，用户可以再点一次；绝不该升级成"断线重连"。
    /// </para>
    /// </summary>
    public Task<AgentIngestAckDto?> ReportScreensAsync(
        AgentScreensDto screens,
        CancellationToken cancellationToken)
        => InvokeIngestAsync(AgentHubClientMethods.ReportScreens, screens, cancellationToken);

    /// <summary>
    /// <b>回报剪贴板内容</b>（M5.5 文字剪贴板）。
    ///
    /// <para>
    /// 与屏清单同一条纪律：这是<b>应答</b>，不是回执。中心下发的
    /// <c>clipboard</c> 指令是 fire-and-forget（<c>SendAsync</c> 对已消失的连接
    /// 静默成功），回执止步于服务进程 —— 只有这条上行能把内容真正送回去。
    /// </para>
    /// </summary>
    public Task<AgentIngestAckDto?> ReportClipboardAsync(
        RemoteClipboardDto clipboard,
        CancellationToken cancellationToken)
        => InvokeIngestAsync(AgentHubClientMethods.ReportClipboard, clipboard, cancellationToken);

    /// <summary>
    /// <b>送一帧远控画面</b>（M5.1）。
    ///
    /// <para>
    /// 🔥 <b>本方法失败会「抛」，这是它与其他上报<b>刻意不同</b>的地方。</b>
    /// 别的上报（遥测、清单、能力）失败一律吞掉，因为"这一条没送到"下次还有机会。
    /// 但码流不同：<b>帧是持续产生的</b>，送不出去还继续收，就等于让被控端
    /// 一直抓屏、一直编码、然后把结果扔掉 —— 白烧一台正在跑业务的机器的 CPU。
    /// 抛出会一路传到接流循环，让它把这路流停掉。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>用 <c>InvokeAsync</c>（要应答）而不是 <c>SendAsync</c>（不等应答）</b>：
    /// 后者在"服务端不认识这个方法"时<b>不会报错</b>（服务端的异常回不到发送方），
    /// 于是旧中心会安静地丢掉每一帧，而客户机以为一切正常、一直烧着 CPU。
    /// 12 fps 下这点往返开销（本机局域网亚毫秒级）远小于"静默空转"的代价。
    /// </para>
    ///
    /// <para>
    /// 🔸 <b>M5.4-c① 起，装不下一条消息的帧会在这里被切成几片送</b>
    /// （<see cref="RemoteFrameChunker"/> —— <b>切片只有那一处实现</b>）。
    /// ⚠️ 装得下的帧<b>仍然走上面那条老路</b>（一整帧一条消息）：
    /// 于是 2160×1440 与 4K 的行为<b>一个字都不变</b> —— 这正是这批改动敢进的理由。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>为什么先 <see cref="RemoteFrameChunker.NeedsChunking"/> 再 <c>Slice</c>，
    /// 而不是直接 "切片、看片数"</b>：<c>Slice</c> 会<b>拷贝</b>字节（每一片是一份新数组）。
    /// 无条件调它 = 给每一帧都加一次全量拷贝，而上面刚说过绝大多数帧根本装得下 ——
    /// 那就等于"为了 8K 把 2160×1440 拖慢了"，与"惰性改动"正好相反。
    /// </para>
    ///
    /// <para>
    /// 🔥 <b>为什么各片是<b>顺序</b> <c>await</c>（而不是并发/不等应答）</b>：
    /// 中心的 <c>RemoteFrameReassembler</c> 是<b>单槽</b>的，而"至多只有一帧不完整"
    /// 这条不变量<b>正是靠"同帧各片顺序 await"撑住的</b>（见那个类的注释）。
    /// 并发发片 = 在一条连接上交错两个帧的片 ⇒ 中心只能把两帧都丢掉。
    /// 代价诚实说：12 片就是 12 次往返，本机局域网亚毫秒级 ⇒ 一帧多花几毫秒，
    /// 而 8K 本来也只有 12 fps（83 ms/帧）的预算。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>旧中心不认识 <c>PushRemoteFrameChunk</c> 时调用会抛</b> ⇒ 按既有纪律
    /// <b>就地把这路流停掉</b>（与 <c>PushRemoteAudio</c> 同一条：不许静默忽略，
    /// 否则变成"编一帧、丢一帧"空烧 CPU）。✅ 但小帧仍走老路 ⇒
    /// <b>旧中心上 4K 及以下照常工作</b>，只有真出现超大帧时才停。
    /// </para>
    /// </summary>
    public Task PushRemoteFrameAsync(
        RemoteStreamFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame, nameof(frame));

        return PushFrameAsync(
            (method, payload, token) => _connection.InvokeAsync<AgentIngestAckDto>(method, payload, token),
            frame,
            cancellationToken,
            KeyframeRequested,
            // ⚠️ 只有这一个生产调用点 —— 阈值<b>只能</b>从这里流进发帧逻辑，
            //    否则会出现"某个入口用默认值、另一个用诊断值"的分裂。
            maxChunkBytes: ChunkThresholdBytes);
    }

    /// <summary>
    /// <b>把一帧送出去的全部逻辑</b>（老路 or 分片路）—— <b>只有一处实现</b>。
    ///
    /// <para>
    /// 🔥 <b>为什么它要收一个"发射器"而不是直接用 <c>_connection</c></b>：
    /// 这段逻辑里有<b>三条会静默错的判据</b>，而它们都只能靠"看着到底发出去了几条什么消息"来验：
    /// </para>
    /// <list type="number">
    ///   <item>小帧走没走老路（= <b>惰性</b>：2160×1440 / 4K 的行为一个字不许变）；</item>
    ///   <item>大帧切出来的片数对不对（<c>ceil(len / 片上限)</c>，不是"差不多"）；</item>
    ///   <item>中心说停时是不是真的停（<b>不许</b>因为"已经发了几片"就降级成警告）。</item>
    /// </list>
    /// <para>
    /// 而 <c>HubConnection</c> 是个具体类、方法不可替，用例造不出它
    /// ⇒ 把"发"这一步抽成一个委托，用例就能拿一个假发射器把这三条全钉住
    /// （<c>AgentFrameChunkingTests</c>）。这与 <c>RemoteSessionCoordinator</c> 的
    /// <c>FrameSink</c> / <c>AudioSink</c> 是同一个套路：<b>接缝开在"谁真正动手"那一步</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它是 <c>internal</c>（只对 <c>ControlServer.Agent.Tests</c> 可见）：
    /// 生产那一侧只有 <see cref="PushRemoteFrameAsync"/> 一个入口，
    /// 免得出现"第二条推帧的路"。
    /// </para>
    /// </summary>
    internal static async Task PushFrameAsync(
        Func<string, object, CancellationToken, Task<AgentIngestAckDto>> invoke,
        RemoteStreamFrame frame,
        CancellationToken cancellationToken,
        Action<string>? onNeedKeyframe = null,
        int maxChunkBytes = RemoteStreamLimits.MaxChunkPayloadBytes)
    {
        // 🔥 **开口要一个关键帧**（中心说"这一帧我拼不起来"时）。
        //
        //    ⚠️ 这里是本仓**唯一一处刻意的空 catch**，理由必须写清楚：
        //    它是一个**补救**动作，而它跑在发帧的路径上 —— 抛出去会被上层
        //    那个 `catch (Exception)` 当成「码流写不出去，接收端已断开」，**整条流就此结束**。
        //    用"一次补救没做成"换"掉一条流"完全不成比例；而且中心只要还在丢帧，
        //    下一次就会被再要一遍。
        //    ⚠️ 所以 `KeyframeRequested` 的实现方<b>别指望"抛出来会有人看见"</b>。
        void AskForKeyframe()
        {
            try
            {
                onNeedKeyframe?.Invoke(frame.Header.SessionId);
            }
            catch
            {
                // 见上：补救失败不许升级成掉线。
            }
        }

        // ── 老路：一整帧一条消息（2160×1440 / 4K 走的就是这条）──
        //
        // ⚠️ 判据用 `maxChunkBytes` 而不是默认值：诊断模式下（阈值被降下来）
        //    连一条 300 KB 的普通帧也要走分片路 —— 那正是端到端冒烟能碰到
        //    分片代码的唯一办法（本机没有 8K 屏）。
        if (!RemoteFrameChunker.NeedsChunking(frame.Data?.Length ?? 0, maxChunkBytes))
        {
            var whole = await invoke(AgentHubClientMethods.PushRemoteFrame, frame, cancellationToken)
                .ConfigureAwait(false);

            // 中心明确说"不收"（拉黑 / 会话已结束 / 帧超限）：
            // 这不是网络问题，重试没有意义 —— 抛出去让这路流停下来。
            //
            // ⚠️ 顺序：**先判拒绝、再判"要不要关键帧"**。会话都没了，
            //    补一个关键帧既没有观看端要，还会往一条正在收场的管道里塞指令。
            if (whole is { Ok: false })
            {
                throw new InvalidOperationException($"中心没有收下这一帧：{whole.Message ?? whole.Status}");
            }

            if (whole.NeedKeyframe) AskForKeyframe();

            return;
        }

        // ── 分片路：只有"一条消息装不下"的帧才走到这里 ──
        //
        // ⚠️ 用 `foreach` 而不是 `for`：片的顺序由 `Slice` 保证（按下标升序），
        //    这里再自己数一遍下标，等于把"顺序"这件事写成两份。
        var needKeyframe = false;

        foreach (var chunk in RemoteFrameChunker.Slice(frame, maxChunkBytes))
        {
            var ack = await invoke(AgentHubClientMethods.PushRemoteFrameChunk, chunk, cancellationToken)
                .ConfigureAwait(false);

            // 与整帧同一条纪律：拒绝 = 刹车信号（会话没了 / 不属于你）⇒ 停掉这路流。
            // ⚠️ **不能**因为"已经发出去几片了"就把它降级成警告：中心说停，就是要停。
            //    那几片会留在中心那个会话的重组槽里，随会话一起被清掉（单槽，有界）。
            if (ack is { Ok: false })
            {
                throw new InvalidOperationException(
                    $"中心没有收下这一帧（第 {chunk.ChunkIndex + 1}/{chunk.ChunkCount} 片）："
                    + $"{ack.Message ?? ack.Status}");
            }

            // 🔥 **按帧累积、循环外只问一次**：中心在同一个故障下会**每片都回**这个标志
            //    （它每次都判"这一帧拼不起来"）。照着每片各要一次，等于把**一次丢帧
            //    放大成一路 IDR** —— 而 8K 的 IDR 正是整条链路上最贵的东西。
            //    所以去重的粒度是**帧**，不是片。
            needKeyframe |= ack.NeedKeyframe;
        }

        if (needKeyframe) AskForKeyframe();
    }

    /// <summary>
    /// <b>送一段远控音频</b>（M5.3）。
    ///
    /// <para>
    /// 🔥 <b>与 <see cref="PushRemoteFrameAsync"/> 同一条纪律：失败<b>会抛</b>。</b>
    /// 理由完全一样 —— 音频也是<b>持续产生</b>的，送不出去还继续采，
    /// 就等于让被控机一直采集、一直编码、然后把结果扔掉。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 与视频同理用 <c>InvokeAsync</c>（要应答）而不是 <c>SendAsync</c>：
    /// 后者在"服务端不认识这个方法"时不会报错（服务端的异常回不到发送方），
    /// 于是旧中心会安静地丢掉每一段音频，而客户机以为一切正常。
    /// 50 fps 下这点往返开销（本机局域网亚毫秒级）远小于"静默空转"的代价。
    /// </para>
    /// </summary>
    public async Task PushRemoteAudioAsync(
        RemoteAudioFrame frame,
        CancellationToken cancellationToken)
    {
        var ack = await _connection
            .InvokeAsync<AgentIngestAckDto>(AgentHubClientMethods.PushRemoteAudio, frame, cancellationToken)
            .ConfigureAwait(false);

        if (ack is { Ok: false })
        {
            throw new InvalidOperationException($"中心没有收下这一段音频：{ack.Message ?? ack.Status}");
        }
    }

    /// <summary>
    /// 上报远控会话的状态变化（M5.1）。
    /// <para>
    /// ⚠️ 与其它上报同一条纪律：<b>失败就吞</b>（返回 null）。
    /// 状态上报失败不该让会话本身崩掉 —— 它最多让页面上的状态晚一拍。
    /// 但停不下来是另一回事，所以真正"该停"的判断不建立在这条消息上。
    /// </para>
    /// </summary>
    public Task<AgentIngestAckDto?> ReportRemoteSessionAsync(
        AgentRemoteSessionStateDto state,
        CancellationToken cancellationToken)
        => InvokeIngestAsync(AgentHubClientMethods.ReportRemoteSession, state, cancellationToken);

    /// <summary>
    /// 回报「配置已应用」（M4.5）。
    /// <para>
    /// 报告的是<b>已经应用成功的版本</b>，不是"收到的版本"：解析失败 / 应用失败时
    /// 不该推进它，否则页面上会显示"已应用 v5"，而机器实际还在跑 v4 的任务。
    /// </para>
    /// </summary>
    public Task<AgentIngestAckDto?> ReportConfigAppliedAsync(
        AgentConfigAppliedDto applied,
        CancellationToken cancellationToken)
        => InvokeIngestAsync(AgentHubClientMethods.ReportConfigApplied, applied, cancellationToken);

    /// <summary>
    /// 把<b>本机那份配置</b>推给中心（2026-10-14 的双向同步）。
    /// <para>
    /// 返回 null = 没拿到应答（连接断了 / 服务端异常）。调用方据此<b>保留</b>"待同步"标志，
    /// 下一次心跳再推 —— 这条消息不能丢：丢了就是"本机改的那一版，中心永远不知道"，
    /// 而现场表现为"本机页面显示一份、中心页面显示另一份"。
    /// </para>
    /// <para>
    /// 应答里的 <c>Config</c> 是<b>中心落库后</b>的那一份（版本号可能被中心 +1、内容可能被归一化）；
    /// 客户机要照它对齐，而不是继续用自己手上那份数字。
    /// </para>
    /// </summary>
    public async Task<AgentConfigSaveResult?> PushConfigAsync(
        AgentConfigPushDto pushed,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _connection
                .InvokeAsync<AgentConfigSaveResult>(
                    AgentHubClientMethods.PushConfig,
                    pushed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // 与心跳同理：这里不抛。"推配置失败"该表现为"下次心跳再试"，而不是"断线重连"。
            return null;
        }
    }

    private async Task<AgentIngestAckDto?> InvokeIngestAsync<T>(
        string method,
        T payload,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _connection
                .InvokeAsync<AgentIngestAckDto>(method, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // 与心跳同理：这里不抛。抛出去会由外层"重连"逻辑处理，
            // 但那样一来"一条坏数据"就会升级成"断线重连"，代价完全不成比例。
            return null;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>拼出 Hub 地址（容忍服务端地址末尾带不带斜杠）。</summary>
    private static string BuildHubUrl(string serverUrl)
    {
        var trimmed = serverUrl.TrimEnd('/');
        return trimmed + Routes.AgentHub;
    }
}

/// <summary>
/// 客户端注册（M4.1）：<b>免凭据</b>登记，换来 agentKey（2026-10-13 起不再需要令牌 / 加入码）。
/// </summary>
public sealed class AgentRegistrar
{
    private readonly HttpClient _http;
    private readonly LogService _log;

    public AgentRegistrar(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// 注册。
    /// </summary>
    /// <returns>成功返回结果；失败返回带原因的 <see cref="AgentRegisterResult"/>（<c>Ok=false</c>）。</returns>
    public async Task<AgentRegisterResult> RegisterAsync(
        string serverUrl,
        AgentRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var url = serverUrl.TrimEnd('/') + "/api/agents/register";

        try
        {
            using var response = await _http
                .PostAsJsonAsync(url, request, cancellationToken)
                .ConfigureAwait(false);

            // 服务端在所有情况下都返回同一个 DTO（含可读原因），所以直接读它 ——
            // 只按状态码猜会让"在线表满了"和"服务端写入失败"这两种完全不同的处置变成同一句"注册失败"。
            var result = await response.Content
                .ReadFromJsonAsync<AgentRegisterResult>(cancellationToken)
                .ConfigureAwait(false);

            if (result is not null) return result;

            return new AgentRegisterResult(
                false,
                $"http{(int)response.StatusCode}",
                $"服务端返回了无法解析的内容（HTTP {(int)response.StatusCode}）。",
                null, null, null, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 只有"调用方主动停"才往上抛（关机 / 换地址）。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 🔥 HttpClient 的 20 秒超时也抛成 OperationCanceledException（TaskCanceledException）。
            //    原来这里无条件 `throw` ⇒ 一个**普通的"服务端不可达"**会一路窜到主循环的
            //    `catch (Exception)`，变成 Error 级「客户端主循环出错」+ 一坨栈
            //    （2026-10-20 复现脚本里就抓到了这一条）。
            //    现场后果不是崩，而是**误导**：看起来像客户端有 bug，实际只是中心没起来；
            //    而且状态栏会显示"运行出错"，而不是"注册被拒绝：无法连接…"。
            _log.Warn("Agent", $"注册请求超时（{serverUrl}）—— 服务端没有在超时内应答。");

            return new AgentRegisterResult(
                false,
                "network",
                $"注册请求超时：{serverUrl} 没有在超时内应答。",
                null, null, null, null, null);
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"注册请求失败：{ex.Message}");

            return new AgentRegisterResult(
                false,
                "network",
                $"无法连接到中心服务端（{serverUrl}）：{ex.Message}",
                null, null, null, null, null);
        }
    }

    /// <summary>
    /// 告知当前中心：<b>本机要换到别的服务端地址去了</b>（2026-10-15，需求①）。
    /// <para>
    /// <b>为什么要有这一步</b>：本机页面改的是"我连谁"，而中心那边只会看到
    /// "这台机器忽然不再心跳了" —— 半小时后被判离线，运维看不出是"主动搬走"还是"掉线了"。
    /// 一条告知把这两件事分开。
    /// </para>
    /// <para>
    /// ⚠️ <b>它刻意不改变服务端的任何状态</b>（不摘除记录、不标记离线）：
    /// 需求口径是"只通知，不阻塞保存"。真正的状态变化交给"它不再心跳"这件事本身。
    /// 也正因为如此，<b>调用方必须吞掉失败</b> —— 中心不可达时正是最需要改地址的时刻，
    /// 那时这条请求注定发不出去，它不该挡住用户。
    /// </para>
    /// </summary>
    /// <returns>给日志/页面用的一句话。</returns>
    public async Task<string> ReportServerChangeAsync(
        string currentServerUrl,
        int agentId,
        string agentKey,
        string newServerUrl,
        CancellationToken cancellationToken)
    {
        var url = $"{currentServerUrl.TrimEnd('/')}/api/agents/{agentId}/server-change";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Headers.TryAddWithoutValidation(AgentAuthentication.HeaderName, agentKey);
            request.Content = JsonContent.Create(new AgentServerChangeRequest(newServerUrl));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? $"已告知当前中心（HTTP {(int)response.StatusCode}）。"
                : $"当前中心没有接受这次告知（HTTP {(int)response.StatusCode}）—— 不影响本机改地址。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"未能告知当前中心（{ex.Message}）—— 不影响本机改地址。";
        }
    }

    /// <summary>
    /// 问一句"中心还认不认这把钥匙"。
    /// <para>
    /// <b>它解决什么</b>：管理员在网页上把一台客户机「移除」之后，服务端就没有那台机器的记录了
    /// —— 这时本地那份 <c>agentKey</c> 在服务端<b>谁也认不出来</b>，重连必然 401。
    /// 而 401 与"网线掉了"在长连接的异常里长得差不多，客户端若一律当成普通连接失败，
    /// 就会拿着这把废钥匙无限重试（退避到 60 秒封顶），永远不再注册
    /// —— 现场就是"移除之后再也加不回来，连重启客户端都没用"。
    /// </para>
    /// <para>
    /// <b>为什么不用异常里的状态码判断</b>：那要先假设 SignalR 把 401 放进了
    /// <c>HttpRequestException.StatusCode</c>（不同版本、不同传输——WebSocket / 长轮询——
    /// 的包装方式并不一致）。这里改成<b>拿一次普通 HTTP 请求去问</b>：
    /// 走的是客户端启动时就在用的那条配置拉取路径，401 的语义没有任何歧义。
    /// 代价只是"连接已经失败之后"多一个请求。
    /// </para>
    /// <para>
    /// ⚠️ <b>只有 401 / 403 才算"被拒"</b>。连不通、超时、服务端 500、404 ——
    /// 一律返回 false（＝"还轮不到作废凭据"）。反过来做会把一次网络抖动
    /// 误判成"我被移除了"，然后白扔一份好凭据、多注册一次。
    /// </para>
    /// </summary>
    /// <param name="budget">
    /// 这次探测最多花多久。<b>默认 5 秒</b>（2026-10-20）。
    /// <para>
    /// 🔥 为什么不再用那个 20 秒的 HttpClient 超时：这个探测只在"连接已经失败<b>之后</b>"跑，
    /// 而中心不可达时它<b>每一轮</b>都会再白等一个完整超时 —— 现场实测一轮四十多秒
    /// （20 秒 TCP + 20 秒探测），"中心启动了、客户端一分多钟才上线"就是这么来的。
    /// 拒绝是一个<b>立刻就能拿到</b>的答复（服务端在认证层直接回 401），5 秒在局域网上绰绰有余。
    /// </para>
    /// </param>
    /// <returns>true = 中心明确拒绝了这把钥匙。</returns>
    public async Task<bool> IsCredentialRejectedAsync(
        string serverUrl,
        int agentId,
        string agentKey,
        CancellationToken cancellationToken,
        TimeSpan? budget = null)
    {
        var url = $"{serverUrl.TrimEnd('/')}/api/agents/{agentId}/config";

        // 与调用方的取消分开：这里多一层"探测预算"，超时只意味着"没问到"。
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(budget ?? DefaultProbeBudget);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // ⚠️ 用 TryAddWithoutValidation：HttpRequestHeaders 的索引器**只有 getter**
            //    （`Headers[name] = …` 编译不过），而 AgentKey 是 base64url 串，
            //    本来也不需要 .NET 去校验它。
            request.Headers.TryAddWithoutValidation(AgentAuthentication.HeaderName, agentKey);

            using var response = await _http.SendAsync(request, probeCts.Token).ConfigureAwait(false);

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 预算用完 = 对面没在 5 秒内答复。它**不是**"被拒"，也不值得记 Warn ——
            // 这条会跟着"中心不可达"每轮出现一次，记 Warn 就是把日志刷满。
            _log.Debug("Agent", $"凭据探测超时（{(budget ?? DefaultProbeBudget).TotalSeconds:0.#} 秒），按「还没被拒」处理。");
            return false;
        }
        catch (Exception ex)
        {
            // 问不到 ≠ 被拒（见上面的 ⚠️）。
            _log.Warn("Agent", $"凭据探测没问出结果（按「还没被拒」处理）：{ex.Message}");
            return false;
        }
    }

    /// <summary>凭据探测的默认预算（见 <see cref="IsCredentialRejectedAsync"/> 的参数说明）。</summary>
    public static readonly TimeSpan DefaultProbeBudget = TimeSpan.FromSeconds(5);
}
