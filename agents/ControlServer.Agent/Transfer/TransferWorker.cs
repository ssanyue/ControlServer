using System.Collections.Concurrent;
using System.Threading.Channels;
using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Transfer;

/// <summary>
/// 客户机侧的<b>传输工作线程</b>（M4.4）：收任务 → 限并发执行 → 回报进度与结果。
/// <para>
/// <b>它的生命周期刻意是"整个进程"而不是"这条长连接"</b>。理由很实际：
/// 搬字节走的是普通 HTTP，与 SignalR 是两条独立的通路 —— 一次心跳抖动把 SignalR 抖断了，
/// 那条正在传的 2GB 文件不该跟着一起死。传输只在<b>进程停机</b>或被<b>吊销</b>时才停。
/// </para>
/// <para>
/// <b>去重是必须的</b>：上线补领（<c>GET /api/agents/transfers</c>）与 SignalR 推送可能对
/// 同一个条目各说一次，而"同一个 <c>ItemId</c> 被两个工作线程同时传"会写出两个
/// <c>.cs-part</c> 互相踩。这里按 <c>ItemId</c> 去重，从入队到终态只放行一次。
/// </para>
/// <para>
/// <b>限并发（默认 2 路）</b>：100 台机器同时被下发时，每台都"来多少传多少"会让
/// 中心侧的连接与磁盘 IO 一起打满；限住之后，慢下来的那些本身就是背压。
/// </para>
/// </summary>
public sealed class TransferWorker : IAsyncDisposable
{
    private readonly HttpClient _control;
    private readonly HttpClient _transfer;
    private readonly TransferOptions _options;
    private readonly string _stateDirectory;
    private readonly LogService _log;

    /// <summary>待办队列（无界但受去重约束；单机同时待办的条目数由中心的批量任务大小决定）。</summary>
    private readonly Channel<AgentTransferTaskDto> _queue =
        Channel.CreateUnbounded<AgentTransferTaskDto>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>已入队 / 在传的条目（去重表）。终态后移除，允许同一 id 日后再次入队。</summary>
    private readonly ConcurrentDictionary<int, byte> _known = new();

    /// <summary>正在执行的条目 → 它自己的取消源。<see cref="AbortActive"/> 靠它掐掉在飞的传输。</summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _active = new();

    private volatile FileTransferClient? _client;
    private readonly bool _enabled;

    public TransferWorker(
        HttpClient control,
        HttpClient transfer,
        TransferOptions options,
        string stateDirectory,
        LogService log)
    {
        _control = control;
        _transfer = transfer;
        _options = options;
        _stateDirectory = stateDirectory;
        _log = log;
        _enabled = options.Enabled;
    }

    /// <summary>本机是否启用了文件传输。</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// 绑定当前会话（连上之后才有 serverUrl / agentKey）。可反复调用；
    /// 换了凭据就换一个客户端，在飞的传输不受影响。
    /// </summary>
    public void SetSession(string serverUrl, string agentKey)
    {
        if (!_enabled) return;

        _client = new FileTransferClient(
            _control, _transfer, serverUrl, agentKey, _options, _stateDirectory, _log);
    }

    /// <summary>
    /// 清会话并中止在飞的传输（被吊销 / 停机时用）。
    /// <para>中止是必要的：吊销的语义就是"立刻停"，让它把剩下 1GB 传完再停不算停。</para>
    /// </summary>
    public void ClearSession()
    {
        _client = null;

        AbortActive();
        DrainQueue();
    }

    /// <summary>入队一条任务（重复的 <c>ItemId</c> 会被静默丢弃）。</summary>
    public void Enqueue(AgentTransferTaskDto task)
    {
        if (!_enabled) return;

        if (task.ItemId <= 0)
        {
            _log.Warn("Transfer", "收到一条没有条目 id 的传输任务（已忽略）。");
            return;
        }

        if (!_known.TryAdd(task.ItemId, 0)) return; // 已排队或在传

        if (_queue.Writer.TryWrite(task)) return;

        _known.TryRemove(task.ItemId, out _);

        _log.Warn("Transfer", $"传输队列已关闭，任务 {task.ItemId} 被丢弃。");
    }

    /// <summary>批量入队（补领时用）。</summary>
    public void EnqueueRange(IEnumerable<AgentTransferTaskDto> tasks)
    {
        foreach (var task in tasks) Enqueue(task);
    }

    /// <summary>
    /// 工作循环：拉起 N 个消费者（N = <see cref="TransferOptions.MaxConcurrent"/>）。
    /// <b>只要没被取消就不返回。</b>
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return Task.CompletedTask;

        var workers = Math.Clamp(_options.MaxConcurrent, 1, 8);

        _log.Info("Transfer", $"文件传输已启用（并发 {workers} 路）。");

        var consumers = new Task[workers];

        for (var i = 0; i < workers; i++) consumers[i] = ConsumeAsync(cancellationToken);

        return Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var task))
                {
                    try
                    {
                        await ProcessAsync(task, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 铁律 #6：一条任务把消费者带死 = 这台机器从此再也收不到文件。
                        _log.Error("Transfer", $"处理传输任务 {task.ItemId} 时出错（已跳过）", ex);
                        _known.TryRemove(task.ItemId, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机路径。
        }
        catch (Exception ex)
        {
            _log.Error("Transfer", "传输工作循环异常退出", ex);
        }
    }

    private async Task ProcessAsync(AgentTransferTaskDto task, CancellationToken cancellationToken)
    {
        var client = _client;

        if (client is null)
        {
            // 还没有凭据（没连上）。任务仍留在服务端，下次上线补领会再拿到 ——
            // 这里只是"忘掉它"，不占用队列。
            _known.TryRemove(task.ItemId, out _);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _active[task.ItemId] = cts;

        try
        {
            await RunWithRetryAsync(client, task, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(task.ItemId, out _);
            _known.TryRemove(task.ItemId, out _);
        }
    }

    private async Task RunWithRetryAsync(
        FileTransferClient client,
        AgentTransferTaskDto task,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Clamp(_options.MaxAttempts, 1, 10);
        var retryDelay = TimeSpan.FromSeconds(Math.Clamp(_options.RetryDelaySeconds, 1, 300));

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                TransferOutcome outcome;

                if (task.Direction == DistributionDirections.Push)
                {
                    _log.Info(
                        "Transfer",
                        $"开始接收下发文件：{task.FileName}（任务 {task.TaskId}，条目 {task.ItemId}）");

                    outcome = await client.DownloadAsync(
                        task,
                        (received, total) => client.ReportProgressAsync(
                            task.ItemId, received, total, null, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                else if (task.Direction == DistributionDirections.Collect)
                {
                    _log.Info(
                        "Transfer",
                        $"开始回传文件：{task.SourcePath}（任务 {task.TaskId}，条目 {task.ItemId}）");

                    outcome = await client.UploadAsync(
                        task,
                        (received, total) => client.ReportProgressAsync(
                            task.ItemId, received, total, null, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await client.ReportCompleteAsync(
                        task.ItemId, false, 0, null, $"未知的传输方向：{task.Direction}", cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                await client.ReportCompleteAsync(
                    task.ItemId, true, outcome.ReceivedBytes, outcome.Sha256, null, cancellationToken)
                    .ConfigureAwait(false);

                _log.Info(
                    "Transfer",
                    $"传输完成：{task.FileName}（{outcome.ReceivedBytes} 字节，条目 {task.ItemId}）");

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停机 / 被吊销：不回报失败 —— 它并不是"失败了"，下次上线会接着续。
                throw;
            }
            catch (Exception ex)
            {
                var fatal = ex is TransferException { Fatal: true };

                if (!fatal && attempt < attempts)
                {
                    _log.Warn(
                        "Transfer",
                        $"传输失败（第 {attempt}/{attempts} 次，{retryDelay.TotalSeconds:0} 秒后重试）：{ex.Message}");

                    try
                    {
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }

                    continue;
                }

                _log.Warn("Transfer", $"传输失败（条目 {task.ItemId}）：{ex.Message}");

                try
                {
                    await client.ReportCompleteAsync(task.ItemId, false, 0, null, ex.Message, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception reportEx)
                {
                    // 回报失败也别抛：明细会留在 running，运维在页面上能看见"卡住的这一条"，
                    // 比"日志里一条异常、页面上什么都没有"清楚得多。
                    _log.Warn("Transfer", $"回报失败结果时又出错（条目 {task.ItemId}）：{reportEx.Message}");
                }

                return;
            }
        }
    }

    private void AbortActive()
    {
        foreach (var pair in _active)
        {
            try
            {
                pair.Value.Cancel();
            }
            catch
            {
                // 取消源已释放：忽略。
            }
        }
    }

    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out var task)) _known.TryRemove(task.ItemId, out _);
        _known.Clear();
    }

    public ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        AbortActive();

        return ValueTask.CompletedTask;
    }
}
