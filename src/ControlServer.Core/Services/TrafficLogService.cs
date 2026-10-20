using ControlServer.Core.Abstractions;
using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 收发日志服务（<b>M2-B 最小可用形态</b>）。
/// <para>
/// 本轮只做两件事：**内存环形缓冲**（供「最近日志」查询）+ **变更事件**（供宿主桥接 SignalR）。
/// 历史落盘（NDJSON 按日期分文件）、日期列表、清空、保留期裁剪由 M2-E 补齐 ——
/// 因此这里刻意不引入 <c>IDataDirectory</c>，避免与 M2-E 的设计打架。
/// </para>
/// <para>
/// <b>稳定优先</b>：<see cref="Write"/> 内部全部 try/catch 吞掉异常，绝不因为一条日志
/// 写失败而影响一次发送的成败判定（对齐 M1 <c>LogService</c> 的既定原则）。
/// </para>
/// </summary>
public sealed class TrafficLogService : ITrafficLog
{
    /// <summary>默认环形缓冲容量（方案 §12 决策 #6：环形 400 条）。</summary>
    public const int DefaultCapacity = 400;

    private readonly int _capacity;
    private readonly Queue<TrafficLogEntry> _buffer = new();
    private readonly object _gate = new();

    public TrafficLogService(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    /// <summary>新增一条收发日志（订阅方负责推送到前端）。</summary>
    public event Action<TrafficLogEntry>? TrafficLogAdded;

    public void Write(TrafficLogEntry entry)
    {
        if (entry is null) return;

        try
        {
            lock (_gate)
            {
                _buffer.Enqueue(entry);
                while (_buffer.Count > _capacity) _buffer.Dequeue();
            }
        }
        catch
        {
            // 缓冲写入失败（理论上不会）不得影响调用方。
            return;
        }

        try
        {
            TrafficLogAdded?.Invoke(entry);
        }
        catch
        {
            // 订阅方异常不得回灌到业务路径。
        }
    }

    /// <summary>便捷重载：直接给定字段，内部负责补时间戳与截断数据展示长度。</summary>
    public void Write(
        string direction,
        string protocol,
        string targetIp,
        int port,
        string? data,
        bool success,
        string? message = null,
        string? buttonName = null,
        string? actionName = null)
        => Write(new TrafficLogEntry(
            BeijingTime.Now,
            direction,
            protocol ?? string.Empty,
            targetIp ?? string.Empty,
            port,
            TrafficLogEntry.TruncateData(data),
            success,
            message ?? string.Empty,
            buttonName ?? string.Empty,
            actionName ?? string.Empty));


    public IReadOnlyList<TrafficLogEntry> Recent(int count = 200)
    {
        if (count <= 0) return Array.Empty<TrafficLogEntry>();

        lock (_gate)
        {
            return _buffer.Reverse().Take(count).Reverse().ToArray();
        }
    }

    public int Count
    {
        get { lock (_gate) { return _buffer.Count; } }
    }

    /// <summary>清空缓冲（保留配置 / 历史文件不动）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
        }
    }
}
