using System.IO.Pipes;
using System.Text.Json;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// <b>会话侧指令通道的服务端</b> —— 跑在<b>托盘进程</b>里（M4.5）。
///
/// <para>
/// 它监听一条命名管道，等中心服务（SYSTEM，Session 0）连上来送一条指令，
/// 在<b>用户会话</b>里把事做掉，再把结果回过去。之所以非要绕这一圈，
/// 是因为「延时启动程序」与「音量」在 Session 0 里做等于没做（见 <see cref="ISessionCommandChannel"/>）。
/// </para>
///
/// <para>
/// ⚠️ <b>权限模型：不设自定义 ACL，用命名管道的默认安全描述符。</b>
/// Win32 的 <c>CreateNamedPipe</c> 在 <c>lpSecurityAttributes = NULL</c> 时给出的默认 DACL 是
/// 「<b>SYSTEM 完全控制 + 管理员完全控制 + 创建者完全控制</b>」。
/// 本管道的创建者就是当前登录用户（托盘），需要连上来的只有 SYSTEM（服务）——
/// 三方恰好都在默认 DACL 里，因此这个默认值就是我们要的东西。
/// </para>
/// <list type="bullet">
///   <item>刻意<b>不</b>引 <c>System.IO.Pipes.AccessControl</c> 包去写一份自定义描述符：
///         那会多一个包、多一份"要跟着权限模型一起维护的"文件，
///         而安全性上并没有变强（Allowed 的就是默认那三方）。</item>
///   <item>默认 DACL 的一条重要后果：<b>同一台机器上的其他普通用户连不上</b>
///         （他们既不是创建者也不是 SYSTEM/管理员），这正是我们要的隔离。</item>
/// </list>
///
/// <para>
/// <b>管道名按会话编号区分</b>（见 <see cref="SessionPipeProtocol.PipeNameForSession"/>），
/// 因此多个用户同时登录时各自有各自的托盘与管道，互不串台。
/// </para>
///
/// <para><b>它绝不抛异常</b>：托盘是 WinExe，任何未捕获异常都会让这个进程悄无声息地消失，
/// 而用户的直观感受是"托盘图标不见了"。所以从建管道到处理请求，每一层都自己吞异常并落一行日志。</para>
/// </summary>
public sealed class SessionPipeServer : IAsyncDisposable
{
    /// <summary>建管道失败后的重试间隔（管道名冲突 / 句柄耗尽等）。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly IAudioController _audio;
    private readonly Func<AgentLaunchTaskDto, SessionCommandResult> _launch;
    private readonly Action<string> _log;
    private readonly RemoteStreamHost? _remoteStream;
    private readonly IClipboardAccess? _clipboard;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private Task _loop = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    /// <param name="pipeName">管道名（见 <see cref="SessionPipeProtocol.PipeNameForSession"/>）。</param>
    /// <param name="audio">音量控制（平台实现；不支持时传 <see cref="UnsupportedAudioController"/>）。</param>
    /// <param name="launch">启动程序的动作。做成委托是为了可测：测试里不必真的拉起进程。</param>
    /// <param name="log">
    /// 极简日志出口。<b>刻意收一个委托而不是 <c>LogService</c></b>：托盘是刻意的"零 DI"进程
    /// （见 <c>Agent.Tray/Program.cs</c> 顶部注释），它只有一个写 <c>%TEMP%</c> 的日志函数。
    /// </param>
    /// <param name="remoteStream">
    /// 远控推流宿主（M5.1）；<c>null</c> = 本进程不提供远控。
    /// <para>
    /// ⚠️ 传 <c>null</c> 时 <c>remote-stream</c> 指令会回一句<b>明确的失败</b>
    /// （"本进程没有挂载远控推流"），而不是静默无反应 —— 后者的表现是
    /// "中心说已下发、托盘毫无动静"，最难查。
    /// </para>
    /// </param>
    public SessionPipeServer(
        string pipeName,
        IAudioController audio,
        Func<AgentLaunchTaskDto, SessionCommandResult> launch,
        Action<string> log,
        RemoteStreamHost? remoteStream = null,
        IClipboardAccess? clipboard = null)
    {
        _pipeName = pipeName;
        _audio = audio;
        _launch = launch;
        _log = log;
        _remoteStream = remoteStream;
        _clipboard = clipboard;
    }

    /// <summary>正在监听的管道名（托盘菜单里显示它，排障时"敲哪个门"一目了然）。</summary>
    public string PipeName => _pipeName;

    /// <summary>开始监听（<b>不阻塞</b>：收连接的循环在自己的后台任务里）。重复调用是空操作。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;

            _started = true;
            _loop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 收连接
    // ═══════════════════════════════════════════════════════════

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            try
            {
                // ⚠️ 单实例（maxNumberOfServerInstances = 1）：同一时刻只处理一条指令。
                //    指令是"按一下音量 / 拉起一个程序"这类低频动作，排队完全无所谓；
                //    而单实例换来的是"先进先出、日志顺序与操作顺序一致"这个很值钱的性质。
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                _log($"会话管道创建失败（{_pipeName}）：{ex.Message} —— {RetryDelay.TotalSeconds:0} 秒后重试。");

                if (!await DelayQuietAsync(RetryDelay, cancellationToken).ConfigureAwait(false)) return;

                continue;
            }

            try
            {
                // 🔥 管道对象**建一次、反复用**（连接之间用 Disconnect）。
                //    如果改成"每处理完一条就 Dispose 再重建"，两次之间会有一段
                //    "管道不存在"的空窗；恰好撞上的服务端会拿到一次连接失败，
                //    表现成"偶发一条指令没反应" —— 最难查的那种。
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 管道对象本身坏了：跳出内层循环重建一个。
                        _log($"会话管道等待连接失败，将重建：{ex.Message}");
                        break;
                    }

                    // HandleAsync 自己吞掉全部异常（见类注释）。
                    await HandleAsync(server, cancellationToken).ConfigureAwait(false);

                    try
                    {
                        // 断开当前连接，管道对象留着继续听下一个。
                        server.Disconnect();
                    }
                    catch (Exception ex)
                    {
                        _log($"会话管道断开失败，将重建：{ex.Message}");
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    server.Dispose();
                }
                catch
                {
                    // 忽略。
                }
            }
        }
    }

    /// <summary>读一条请求、执行、回一条结果。<b>永不抛</b>。</summary>
    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SessionPipeProtocol.RequestTimeoutMs);

        string? line;

        try
        {
            using var reader = new StreamReader(
                server,
                SessionPipeProtocol.Utf8NoBom,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 托盘正在退出：正常路径。
            return;
        }
        catch (OperationCanceledException)
        {
            // 连上了却一直不说话。可能是探测连接，也可能是个卡住的客户端 ——
            // 后者要留痕，否则"某次指令没反应"就永远查不出是谁占着管道。
            _log($"有连接进来但 {SessionPipeProtocol.RequestTimeoutMs} 毫秒内没有发出任何内容，已放弃。");
            return;
        }
        catch (Exception ex)
        {
            _log($"读取会话指令失败：{ex.Message}");
            return;
        }

        // 🔥 null = 对方连上就断开了（**服务侧的"可用性探测"就是这么干的**）。
        //    这一条必须静默：服务每几十秒探一次，逐条记日志会把托盘日志刷爆，
        //    而真正的问题（那条超时的）反而被埋在噪音里。
        if (line is null) return;

        var result = Execute(line);

        _log(result.Ok
            ? $"已执行：{result.Message}"
            : $"未执行：{result.Message}");

        try
        {
            using var writer = new StreamWriter(
                server,
                SessionPipeProtocol.Utf8NoBom,
                bufferSize: 4096,
                leaveOpen: true);

            await writer.WriteLineAsync(SessionPipeProtocol.Serialize(result)).ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正在退出，回不回去都无所谓。
        }
        catch (OperationCanceledException)
        {
            _log("回写会话指令结果超时（对方可能已经断开）。");
        }
        catch (Exception ex)
        {
            _log($"回写会话指令结果失败：{ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 执行
    // ═══════════════════════════════════════════════════════════

    /// <summary>把一行 JSON 变成一次动作。<b>永不抛</b>。</summary>
    private SessionCommandResult Execute(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return SessionCommandResult.Failure("收到空请求。");

        SessionCommandRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<SessionCommandRequest>(line, SessionPipeProtocol.Json);
        }
        catch (JsonException ex)
        {
            return SessionCommandResult.Failure($"请求不是合法 JSON：{ex.Message}");
        }

        if (request is null) return SessionCommandResult.Failure("请求解析为空。");

        return request.Method switch
        {
            SessionCommandMethods.Volume => ApplyVolume(request.Argument),
            SessionCommandMethods.Launch => Launch(request.Argument),
            SessionCommandMethods.RemoteStream => ApplyRemoteStream(request.Argument),
            SessionCommandMethods.Screens => DescribeScreens(),
            SessionCommandMethods.Clipboard => ApplyClipboard(request.Argument),

            _ => SessionCommandResult.Failure($"不认识的会话侧指令：{request.Method ?? "(空)"}"),
        };
    }

    /// <summary>
    /// 远控指令（M5.1）：把参数交给 <see cref="RemoteStreamHost"/>。
    /// <para>
    /// ⚠️ <b>参数解析失败必须报出来</b>，不能当"空指令"忽略：
    /// 这条链路上游是中心的 HTTP 请求，一旦参数格式对不上（版本不一致），
    /// 静默忽略的表现是"点了开始远控，什么也没发生"。
    /// </para>
    /// </summary>
    private SessionCommandResult ApplyRemoteStream(string? argument)
    {
        if (_remoteStream is null)
        {
            return SessionCommandResult.Failure(
                "本进程没有挂载远控推流能力（托盘未接线）—— 这台机器上的远控用不了。");
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            return SessionCommandResult.Failure("远控指令缺少参数。");
        }

        RemoteStreamRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<RemoteStreamRequest>(argument, SessionPipeProtocol.Json);
        }
        catch (JsonException ex)
        {
            return SessionCommandResult.Failure($"远控参数不是合法 JSON：{ex.Message}");
        }

        if (request is null) return SessionCommandResult.Failure("远控参数为空。");

        return _remoteStream.Handle(request);
    }

    /// <summary>
    /// 屏清单（M5.5）：**问的是当前这个用户会话的显示器**。
    ///
    /// <para>
    /// 🔥 <b>为什么失败要分两种说法</b>：①"本进程没挂远控能力"（托盘没接线 / 版本过旧）
    /// 与 ②"挂上了但枚举不到任何屏"（这个会话没有桌面）—— 前者去升级客户端，
    /// 后者去看那台机器有没有人登录。合并成一句"取不到屏清单"，现场会去重装客户端。
    /// </para>
    /// </summary>
    private SessionCommandResult DescribeScreens()
    {
        if (_remoteStream is null)
        {
            return SessionCommandResult.Failure(
                "本进程没有挂载远控推流能力（托盘未接线）—— 报不出屏清单。");
        }

        return _remoteStream.DescribeScreens();
    }

    /// <summary>
    /// 读写本机剪贴板（M5.5）。参数是 <c>RemoteStreamRequest</c> 的 JSON。
    /// <para>
    /// ⚠️ **不依赖远控会话**：剪贴板是这台机器的全局资源，
    /// 即使这一路会话正在重建编码器，也应该读得到。
    /// </para>
    /// </summary>
    private SessionCommandResult ApplyClipboard(string? argument)
    {
        if (_clipboard is null)
        {
            return SessionCommandResult.Failure(
                "本进程没有挂载剪贴板访问能力（托盘未接线）—— 这台机器上的剪贴板同步用不了。");
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            return SessionCommandResult.Failure("剪贴板指令缺少参数。");
        }

        RemoteStreamRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<RemoteStreamRequest>(argument, SessionPipeProtocol.Json);
        }
        catch (JsonException ex)
        {
            return SessionCommandResult.Failure($"剪贴板参数不是合法 JSON：{ex.Message}");
        }

        if (request is null) return SessionCommandResult.Failure("剪贴板参数为空。");

        if (string.Equals(request.ClipboardAction, RemoteClipboardActions.Set, StringComparison.OrdinalIgnoreCase))
        {
            var text = request.ClipboardText;

            if (text is null)
            {
                return SessionCommandResult.Failure("写入剪贴板时没有带文本。");
            }

            if (text.Length > RemoteClipboardLimits.MaxChars)
            {
                // ⚠️ 拒绝而不是截断：截断之后对方粘出来少一半，而且看不出少了。
                return SessionCommandResult.Failure(
                    $"这段文字有 {text.Length} 个字符，超过上限 {RemoteClipboardLimits.MaxChars} —— 已拒绝。");
            }

            return _clipboard.WriteText(text, out var writeNote)
                ? SessionCommandResult.Success($"已写入剪贴板（{text.Length} 个字符）。")
                : SessionCommandResult.Failure(writeNote ?? "写入剪贴板失败。");
        }

        if (string.Equals(request.ClipboardAction, RemoteClipboardActions.Get, StringComparison.OrdinalIgnoreCase))
        {
            var read = _clipboard.ReadText(out var readNote);

            // 🔥 **读失败也走载荷，不走 Failure**（2026-10-18 浏览器实证抓到的第二个坑）：
            //
            //    `Failure(...)` 那句话只活在本机的 `[Session] 未执行：…` 日志里 ——
            //    中心到被控端是 fire-and-forget，中心收不到这个结果，
            //    而页面靠的是 `ClipboardChanged` 那条**推送**。
            //    ⇒ 走 Failure 的话，"读取失败"这件事**页面上根本不会发生**：
            //       用户点一下「读取被控机的」，页面还挂着上一次的内容，
            //       看起来就是"点了没反应"。
            //
            //    ⇒ 所以三种情况统一由载荷表达（这正是 `RemoteClipboardDto` 三态的设计）：
            //       有文本 / 没有文本（text=null, note=null）/ 读失败（text=null, note=原因）。
            var failure = read is null && readNote is not null;

            var truncated = read is { Length: > RemoteClipboardLimits.MaxChars };

            var payload = JsonSerializer.Serialize(
                new RemoteClipboardDto(
                    truncated ? read![..RemoteClipboardLimits.MaxChars] : read,
                    // 🔴 截断必须说出来：对方看到的是一段"看起来完整"的文本，
                    //    而结尾少掉的那半句没有任何痕迹。
                    //
                    // 🔴 **"没有文本"这一格必须是 null，一个字的解释都不许写进去。**
                    //    这个字段的语义是「**读失败的原因**」，而 `text is null` 已经完整表达了
                    //    "没有文本"（有图片 / 空 / 被别的程序占着）—— 它不是故障。
                    //    ⚠️ 原先这里写了一句"被控机剪贴板里此刻没有文本（可能是图片、为空，
                    //    或被别的程序占着）。"，于是页面按"有 note ⇒ 读失败"渲染成
                    //    「读取失败：被控机剪贴板里此刻没有文本…」—— 一句话自相矛盾，
                    //    而现场会照着"读取失败"去查一个根本没坏的东西。
                    Note: truncated
                        ? $"剪贴板有 {read!.Length} 个字符，超过上限 —— 只带回前 {RemoteClipboardLimits.MaxChars} 个。"
                        : readNote,
                    At: BeijingTime.Now,
                    Truncated: truncated),
                SessionPipeProtocol.Json);

            // 本机日志仍然要留下"这次没读到"的证据（现场排障第一眼就是看它）。
            return SessionCommandResult.SuccessWithPayload(
                payload,
                failure ? $"剪贴板读取失败：{readNote}（原因已报给中心）" : null);
        }

        return SessionCommandResult.Failure($"不认识的剪贴板动作「{request.ClipboardAction ?? "(空)"}」（应为 get / set）。");
    }

    private SessionCommandResult ApplyVolume(string? action)
    {
        if (!AgentVolumeActions.IsValid(action))
        {
            return SessionCommandResult.Failure($"音量参数不合法：{action ?? "(空)"}（应为 up / down / mute / unmute）");
        }

        if (!_audio.IsAvailable)
        {
            return SessionCommandResult.Failure($"本机无法控制音量：{_audio.AvailabilityNote}");
        }

        var hostAction = action switch
        {
            AgentVolumeActions.Up => HostAudioAction.VolumeUp,
            AgentVolumeActions.Down => HostAudioAction.VolumeDown,
            AgentVolumeActions.Mute => HostAudioAction.Mute,
            AgentVolumeActions.Unmute => HostAudioAction.Unmute,

            // IsValid 已经放行过，这里不可达；写成显式的失败是为了以后加动作时不会静默走错分支。
            _ => (HostAudioAction?)null,
        };

        if (hostAction is null) return SessionCommandResult.Failure($"音量参数不合法：{action}");

        // 🔥 成功后回的是**操作之后从系统读回来的真实状态**（`IAudioController` 的约定），
        //    而不是"我们以为它变成了什么样"。现场问"音量到底调到位没有"时，这才是答案。
        var result = _audio.Apply(hostAction.Value);

        return result.Ok
            ? SessionCommandResult.Success(result.Message)
            : SessionCommandResult.Failure(result.Message);
    }

    private SessionCommandResult Launch(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return SessionCommandResult.Failure("启动指令缺少参数。");

        AgentLaunchTaskDto? task;

        try
        {
            task = JsonSerializer.Deserialize<AgentLaunchTaskDto>(argument, SessionPipeProtocol.Json);
        }
        catch (JsonException ex)
        {
            return SessionCommandResult.Failure($"启动参数不是合法 JSON：{ex.Message}");
        }

        if (task is null) return SessionCommandResult.Failure("启动参数为空。");

        return _launch(task);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>可取消的等待；返回 false 表示已被取消（调用方应当直接收工）。</summary>
    private static async Task<bool> DelayQuietAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 收连接循环内部已全量捕获。
        }

        _lifetime.Dispose();
    }
}
