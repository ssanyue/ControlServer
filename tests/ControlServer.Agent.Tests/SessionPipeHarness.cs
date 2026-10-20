using System.IO.Pipes;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 会话侧命名管道的测试台：<b>一头真的起服务端（托盘侧），一头真的用客户端（服务侧）去连</b>，
/// 中间是货真价实的 Windows 命名管道。
///
/// <para>
/// <b>为什么不 mock 掉管道</b>：这条通道最容易出问题的恰恰不是"逻辑"，而是
/// 行分隔、UTF-8 无 BOM、连接 / 断开时机、单实例排队这些<b>只有真管道才会暴露</b>的东西。
/// 把管道换成内存流之后，测的就只剩"我自己写的假管道"了。
/// </para>
/// </summary>
internal sealed class SessionPipeHarness : IAsyncDisposable
{
    /// <summary>每个测试台一个独立会话编号 ⇒ 独立管道名，测试之间不会互相串台。</summary>
    private static int _sessionSeed = 900;

    private readonly string _root;
    private readonly LogService _log;
    private readonly SessionPipeServer _server;
    private readonly SessionPipeChannel _channel;
    private readonly object _logGate = new();
    private readonly List<string> _serverLines = new();

    public SessionPipeHarness()
    {
        SessionId = Interlocked.Increment(ref _sessionSeed);

        PipeName = SessionPipeProtocol.PipeNameForSession(SessionId);

        _root = Path.Combine(
            Path.GetTempPath(),
            "cs-agent-pipe-tests",
            Guid.NewGuid().ToString("N"));

        var dataDirectory = new TestDataDirectory(_root);
        dataDirectory.EnsureCreated();

        _log = new LogService(dataDirectory, capacity: 200, flushIntervalMs: 60_000);

        _server = new SessionPipeServer(
            PipeName,
            Audio,
            task =>
            {
                lock (_logGate) LaunchedTasks.Add(task);

                return LaunchReply(task);
            },
            message =>
            {
                lock (_logGate) _serverLines.Add(message);
            });

        _channel = new SessionPipeChannel(() => SessionId, _log);

        _server.Start();

        // ⚠️ 必须等管道真的建起来再交出去：Start() 只是把收连接循环丢进后台，
        //    而"建管道"发生在那个循环的第一轮里。少了这次等待，
        //    第一个断言就会随机地撞上"管道还不存在"，表现为偶发红灯。
        //    这次预热连接本身走的是"连上就断"的探测形态，被服务端静默忽略（见 SessionPipeServer）。
        WaitUntilListening();
    }

    private void WaitUntilListening()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                probe.Connect(50);

                return;
            }
            catch
            {
                Thread.Sleep(20);
            }
        }

        throw new InvalidOperationException($"测试台没能在 5 秒内把命名管道建起来：{PipeName}");
    }

    /// <summary>服务端（托盘侧）看到的日志行。</summary>
    public IReadOnlyList<string> ServerLines
    {
        get
        {
            lock (_logGate) return _serverLines.ToArray();
        }
    }

    /// <summary>客户端（服务侧）写进 <see cref="LogService"/> 的日志行。</summary>
    public IReadOnlyList<string> ChannelLines
        => _log.Recent(200).Select(entry => $"[{entry.Category}] {entry.Message}").ToArray();

    public int SessionId { get; }

    public string PipeName { get; }

    /// <summary>假的音量控制器：只记录被调用了什么，不碰真的音频端点。</summary>
    public StubAudioController Audio { get; } = new();

    /// <summary>服务端收到的启动任务（原样反序列化出来的那个对象）。</summary>
    public List<AgentLaunchTaskDto> LaunchedTasks { get; } = new();

    /// <summary>让测试可以替换"启动"的返回值（默认成功）。</summary>
    public Func<AgentLaunchTaskDto, SessionCommandResult> LaunchReply { get; set; }
        = task => SessionCommandResult.Success($"已启动 {task.ProgramPath}");

    public SessionPipeChannel Channel => _channel;

    /// <summary>
    /// 另建一个客户端（服务侧通道），会话编号由测试自己控制。
    /// <para>用于覆盖"没有交互式会话""敲到一个没有托盘的会话"这类分支。</para>
    /// </summary>
    public SessionPipeChannel CreateChannel(Func<int?> activeSession) => new(activeSession, _log);

    /// <summary>
    /// <b>裸连一次管道</b>，按协议写一行（可为 null = 只连不说话）并读回一行。
    /// <para>
    /// 存在的理由：<c>SessionPipeChannel</c> 只会发合法请求，
    /// 而"发一坨坏 JSON""发一个空行""连上就不说话"这些边界必须绕开它才能构造。
    /// </para>
    /// </summary>
    public static async Task<string?> RawAsync(string pipeName, string? payload, int timeoutMs = 3000)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        client.Connect(timeoutMs);

        if (payload is not null)
        {
            using (var writer = new StreamWriter(client, SessionPipeProtocol.Utf8NoBom, 4096, leaveOpen: true))
            {
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        using var reader = new StreamReader(
            client,
            SessionPipeProtocol.Utf8NoBom,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        return await reader.ReadLineAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        await _log.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响测试结论。
        }
    }
}

/// <summary>只记录的假音量控制器（行为可切换，用于覆盖"设备不支持"这条分支）。</summary>
internal sealed class StubAudioController : IAudioController
{
    private readonly List<HostAudioAction> _actions = new();
    private readonly object _gate = new();

    public bool IsAvailable { get; set; } = true;

    public string AvailabilityNote { get; set; } = string.Empty;

    /// <summary>下一次 <c>Apply</c> 是否报失败。</summary>
    public string? FailWith { get; set; }

    public IReadOnlyList<HostAudioAction> Actions
    {
        get
        {
            lock (_gate) return _actions.ToArray();
        }
    }

    public HostAudioState Read() => new(40, false);

    public HostAudioResult Apply(HostAudioAction action)
    {
        lock (_gate) _actions.Add(action);

        return FailWith is { } reason
            ? HostAudioResult.Failure(reason)
            : HostAudioResult.Success($"{action}：50%（未静音）", new HostAudioState(50, false));
    }
}
