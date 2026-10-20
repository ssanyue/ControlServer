using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 日志服务：内存环形缓冲（供"最近日志"查询）+ 批量落盘（NDJSON，按日期分文件）+ 实时事件。
///
/// 设计要点（稳定优先）：
///  - <see cref="LogAdded"/> 事件供上层桥接到 SignalR；
///  - 落盘走 <see cref="ConcurrentQueue{T}"/> + 定时批量 append，不阻塞调用方；
///  - 落盘失败绝不上抛、绝不拖垮宿主。
///
/// <para>
/// <b>M6.5 补的两件事</b>：
/// ① <b>保留策略</b>（<see cref="LogRetention"/>）：每天第一次落盘前清理超龄/超量文件。
/// 在这之前日志<b>只增不减</b>，磁盘满的第一个信号是"日志悄悄断了"。
/// ② <b>写失败可见</b>：catch 仍然是"不拖垮业务"，但连续失败从 0 变 1 时
/// 通过 <see cref="SinkFailed"/> 往外喊一声（宿主接 Serilog），恢复时再喊一声。
/// 改造前这两件事都看不见 —— 那正是"日志断了"最难查的原因。
/// </para>
/// </summary>
public sealed class LogService : IAsyncDisposable
{
    private readonly IDataDirectory _dataDirectory;
    private readonly int _capacity;
    private readonly LogRetentionOptions _retention;
    private readonly Queue<LogEntry> _buffer = new();
    private readonly object _gate = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _flushTimer;
    private volatile bool _disposed;

    /// <summary>
    /// 落盘用的序列化选项。
    ///
    /// <para>
    /// 🔥 <b>必须关掉"非 ASCII 全部转义"</b>（默认会把「日志保留」写成 <c>\u65E5\u5FD7\u4FDD\u7559</c>）。
    /// 这不是美观问题：现场排查的第一个动作是
    /// <c>findstr 授权 logs\log-2026-10-17.ndjson</c>，转义之后<b>一个字都匹配不上</b> ——
    /// 人会据此得出"日志里没有这条"的错误结论，然后往完全错的方向查。
    /// 这一条是在 M6.5 的实测里撞出来的（干净装一台、grep 自己的日志、grep 不到）。
    /// </para>
    /// <para>
    /// 安全性：这些文件只在磁盘上被 grep / 被打包进备份，<b>从不作为 HTML 渲染</b>。
    /// HTTP 侧的 JSON 走 MVC 自己的序列化器，不受这里影响 ——
    /// 所以放宽转义不会把注入面带到页面上去。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>已执行过保留策略的日期（北京日期）。0 = 还没跑过。</summary>
    private int _prunedDayNumber;

    /// <summary>落盘连续失败次数（成功后归零）。</summary>
    private int _consecutiveFailures;

    private string? _lastFlushError;

    public LogService(
        IDataDirectory dataDirectory,
        int capacity = 5000,
        int flushIntervalMs = 500,
        LogRetentionOptions? retention = null)
    {
        _dataDirectory = dataDirectory;
        _capacity = capacity;
        _retention = retention ?? LogRetentionOptions.Default;
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, flushIntervalMs, flushIntervalMs);
    }

    public event Action<LogEntry>? LogAdded;

    /// <summary>
    /// 落盘<b>连续失败从 0 变 1</b>时触发一次（参数：一句人话 + 原始异常）。
    /// <para>
    /// 🔴 宿主必须把它接到<b>另一条通道</b>（这里是 Serilog → <c>server-*.log</c> + 控制台）。
    /// 接回 <see cref="Write"/> 等于没接：写不进去的正是这条通道本身。
    /// </para>
    /// </summary>
    public event Action<string, Exception?>? SinkFailed;

    /// <summary>落盘从"连续失败"恢复成功时触发一次。</summary>
    public event Action? SinkRecovered;

    /// <summary>当前连续失败次数（0 = 正常）。<c>/healthz</c> 与巡检命令读它。</summary>
    public int ConsecutiveFlushFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>最后一次落盘失败的原因；正常时为 <c>null</c>。</summary>
    public string? LastFlushError => Volatile.Read(ref _lastFlushError);

    /// <summary>生效中的保留策略（巡检命令照着它判断"到底有没有在清理"）。</summary>
    public LogRetentionOptions Retention => _retention;

    public void Write(LogLevelKind level, string category, string message, Exception? exception = null)
    {
        if (_disposed) return;

        var text = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        var entry = new LogEntry(BeijingTime.Now, level, category, text);

        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > _capacity) _buffer.Dequeue();
        }

        _pending.Enqueue(entry);
        LogAdded?.Invoke(entry);
    }

    public void Debug(string category, string message) => Write(LogLevelKind.Debug, category, message);

    public void Info(string category, string message) => Write(LogLevelKind.Info, category, message);

    public void Warn(string category, string message) => Write(LogLevelKind.Warning, category, message);

    public void Error(string category, string message, Exception? exception = null) => Write(LogLevelKind.Error, category, message, exception);

    /// <summary>取最近 count 条（时间正序）。</summary>
    public IReadOnlyList<LogEntry> Recent(int count = 200)
    {
        lock (_gate)
        {
            return _buffer.Reverse().Take(count).Reverse().ToArray();
        }
    }

    public int Count
    {
        get { lock (_gate) { return _buffer.Count; } }
    }

    public async Task FlushAsync()
    {
        if (_disposed && _pending.IsEmpty) return;
        if (!await _flushLock.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            // ── 保留策略：每天第一次落盘前跑一次 ──
            //
            // 放在写之前而不是之后，是刻意的：磁盘满的时候，清理旧日志正是解药。
            // 放在写之后，最需要它的那一刻反而永远轮不到。
            PruneIfDue();

            if (_pending.IsEmpty) return;

            Directory.CreateDirectory(_dataDirectory.LogDirectory);
            var file = Path.Combine(_dataDirectory.LogDirectory, $"log-{BeijingTime.Now:yyyy-MM-dd}.ndjson");
            var sb = new StringBuilder();
            while (_pending.TryDequeue(out var entry))
            {
                sb.AppendLine(JsonSerializer.Serialize(entry, FileJsonOptions));
            }

            if (sb.Length > 0)
            {
                await File.AppendAllTextAsync(file, sb.ToString()).ConfigureAwait(false);
            }

            MarkFlushOk();
        }
        catch (Exception ex)
        {
            // 日志落盘失败不得影响业务：静默丢弃本轮批量，下轮继续。
            // ⚠️ 但"静默"只指**不抛给调用方** —— 连续失败必须能被外面看见，
            //    否则现场症状是"日志凭空断了"，而磁盘满、目录被 ACL 锁死、
            //    文件被别的进程独占，全都长得一模一样。
            MarkFlushFailed(ex);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// 今天第一次落盘时执行一次保留策略（<see cref="LogRetention"/>）。
    /// <para>时间由 <see cref="BeijingTime"/> 取，与其他时间戳同源。</para>
    /// </summary>
    private void PruneIfDue()
    {
        if (!_retention.Enabled) return;

        var today = DateOnly.FromDateTime(BeijingTime.Now.DateTime);
        var dayNumber = today.DayNumber;

        // 同一天只跑一次：这里每 500 ms 被调一次，每次都枚举目录是不必要的。
        // 不需要 CAS —— 整段 FlushAsync 已经在 _flushLock 里串行。
        if (Volatile.Read(ref _prunedDayNumber) == dayNumber) return;
        Volatile.Write(ref _prunedDayNumber, dayNumber);

        var outcome = LogRetention.Prune(_dataDirectory.LogDirectory, _retention, today);

        if (outcome.HasDeletions)
        {
            Info(
                "Log",
                $"日志保留：按策略删掉 {outcome.DeletedFiles} 个过期文件（{Human(outcome.DeletedBytes)}），"
                + $"留下 {outcome.KeptFiles} 个（{Human(outcome.KeptBytes)}）。"
                + $"策略 = 保留 {_retention.RetentionDays} 天 / 上限 {_retention.MaxTotalMegabytes} MB。");
        }

        foreach (var error in outcome.Errors)
        {
            Warn("Log", $"日志保留：{error}");
        }
    }

    /// <summary>
    /// 人读的字节数。小于 1 MB 时给 KB 而不是 <c>0.0 MB</c> ——
    /// 实测里"删掉 2 个过期文件（0.0 MB）"看起来像"什么都没删"，
    /// 而那句话本来是用来证明清理发生了的。
    /// </summary>
    private static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    private void MarkFlushOk()
    {
        if (Interlocked.Exchange(ref _consecutiveFailures, 0) == 0) return;

        Volatile.Write(ref _lastFlushError, null);
        Raise(SinkRecovered);
    }

    private void MarkFlushFailed(Exception ex)
    {
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        Volatile.Write(ref _lastFlushError, $"{ex.GetType().Name}: {ex.Message}");

        // 只在"从正常变为失败"那一次喊。失败持续时每 500 ms 喊一次会把
        // server-*.log 也刷爆 —— 那等于用日志问题制造第二个日志问题。
        if (failures != 1) return;

        Raise(
            SinkFailed,
            $"业务日志落盘失败（{_dataDirectory.LogDirectory}）：{ex.GetType().Name}: {ex.Message}。"
            + "最常见的原因是磁盘满、目录权限被收紧、或文件被别的进程独占。"
            + "注意：这意味着「最近日志」页面仍在更新，但落盘文件已经停止增长。",
            ex);
    }

    /// <summary>调外部回调，绝不让回调的异常反向污染落盘路径。</summary>
    private void Raise(Action<string, Exception?>? handler, string message, Exception? ex)
    {
        try
        {
            handler?.Invoke(message, ex);
        }
        catch
        {
            // 告警通道自己坏了也不能影响业务，更不能把 FlushAsync 带崩。
        }
    }

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _flushTimer.DisposeAsync().ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
        _flushLock.Dispose();
    }
}
