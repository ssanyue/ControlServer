using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Telemetry;

/// <summary>
/// 遥测的**本地水位**（M4.2）。
/// <para>
/// 只记三件"重启后必须还记得"的事：
/// </para>
/// <list type="number">
///   <item><b>开关机事件读到哪条了</b>（<c>RecordId</c>）—— 记不住就会每次重启把 7 天的
///         事件重扫一遍，靠服务端去重兜着，日志与网络都被无谓刷满；</item>
///   <item><b>上次做首次回溯是什么时候</b> —— 用来判断"第一次跑"还是"第 N 次跑"；</item>
///   <item><b>上次上报硬件清单是什么时候</b> —— 清单 6 小时一次，重启不该立刻重报。</item>
/// </list>
/// <para>
/// <b>为什么不用"最近一次上报时间"当水位</b>：客户机时钟会被校正（甚至被手工回拨）。
/// 以时间为水位时，一次"把表拨回去两小时"就会让后面两小时的新事件全被当成旧的丢掉。
/// 事件日志的 <c>RecordId</c> 单调递增，不受时钟影响。
/// </para>
/// <para>
/// 文件是<b>明文 JSON</b>（不含任何凭据），放状态目录，与 <c>credentials.dat</c> 同级。
/// </para>
/// </summary>
public sealed class AgentTelemetryState
{
    /// <summary>已上报过的最大事件日志记录号（null = 还没读过）。</summary>
    [JsonPropertyName("powerEventRecordId")]
    public long? PowerEventRecordId { get; set; }

    /// <summary>上次成功提交开关机事件账目的时刻（本机钟，仅用于日志与排障）。</summary>
    [JsonPropertyName("powerEventCheckedAt")]
    public DateTimeOffset? PowerEventCheckedAt { get; set; }

    /// <summary>是否已经做过首次回溯（决定下一次读事件时用"回溯 N 天"还是"小重叠窗口"）。</summary>
    [JsonPropertyName("powerEventBackfilled")]
    public bool PowerEventBackfilled { get; set; }

    /// <summary>上次成功上报硬件清单的时刻。</summary>
    [JsonPropertyName("inventoryReportedAt")]
    public DateTimeOffset? InventoryReportedAt { get; set; }

    public AgentTelemetryState Clone() => new()
    {
        PowerEventRecordId = PowerEventRecordId,
        PowerEventCheckedAt = PowerEventCheckedAt,
        PowerEventBackfilled = PowerEventBackfilled,
        InventoryReportedAt = InventoryReportedAt,
    };
}

/// <summary>
/// <see cref="AgentTelemetryState"/> 的读写（M4.2）。
/// <para>
/// <b>原子写</b>：先写 <c>.tmp</c> 再 <c>File.Move(overwrite)</c>。
/// 直接覆盖原文件时，若在"截断后、写完前"断电（客户机是常年通电的展厅机器，
/// 现场跳闸很常见），会留下一个 0 字节文件 ⇒ 下次启动解析失败，水位归零，
/// 于是把 7 天事件重扫一遍。原子替换让文件只可能是"旧内容"或"新内容"。
/// </para>
/// <para>
/// <b>读失败一律降级为"空水位"而不是抛</b>：文件损坏最多让客户端多做一次回溯，
/// 而抛出去会让整个上报循环停摆 —— 两害相权取其轻。
/// </para>
/// </summary>
public sealed class TelemetryStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LogService _log;
    private readonly object _gate = new();

    private AgentTelemetryState _state;

    public TelemetryStateStore(string stateDirectory, LogService log)
    {
        _log = log;
        // ⚠️ 本文件里**不要**取一个叫 Path 的成员：那会把 System.IO.Path 遮掉，
        //    于是同一份代码里的 Path.Combine 会变成"在 string 上找 Combine"（CS1061）。
        FilePath = System.IO.Path.Combine(stateDirectory, "telemetry-state.json");
        _state = Load();
    }

    /// <summary>当前水位的快照（拷贝，调用方随便改）。</summary>
    public AgentTelemetryState Snapshot()
    {
        lock (_gate) return _state.Clone();
    }

    /// <summary>
    /// 改水位并落盘。
    /// <para>传进来的委托在锁内执行 —— 别在里面做 IO 或 await。</para>
    /// </summary>
    public void Update(Action<AgentTelemetryState> mutate)
    {
        AgentTelemetryState snapshot;

        lock (_gate)
        {
            mutate(_state);
            snapshot = _state.Clone();
        }

        Save(snapshot);
    }

    /// <summary>文件路径（排障时要在日志里说出来）。</summary>
    public string FilePath { get; }

    private AgentTelemetryState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AgentTelemetryState();

            var json = File.ReadAllText(FilePath);

            if (string.IsNullOrWhiteSpace(json)) return new AgentTelemetryState();

            return JsonSerializer.Deserialize<AgentTelemetryState>(json, JsonOptions)
                   ?? new AgentTelemetryState();
        }
        catch (Exception ex)
        {
            _log.Warn("Agent", $"遥测水位文件读取失败（按「首次运行」处理）：{ex.Message}");
            return new AgentTelemetryState();
        }
    }

    private void Save(AgentTelemetryState state)
    {
        var temp = FilePath + ".tmp";

        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));

            // overwrite: true ⇒ 目标已存在也能替换（Windows 上走 MoveFileEx REPLACE_EXISTING）。
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 铁律 #6：后台任务绝不因异常静默死掉；水位写不进去也只是"下次多扫一遍"。
            _log.Warn("Agent", $"遥测水位文件写入失败（下次重启可能重复上报）：{ex.Message}");

            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 临时文件删不掉就算了，下次写入会覆盖它。
            }
        }
    }
}
