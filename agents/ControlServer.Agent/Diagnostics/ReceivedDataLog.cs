using ControlServer.Contracts;
using ControlServer.Core.Services;

namespace ControlServer.Agent.Diagnostics;

/// <summary>
/// 客户机内存里的<b>「接收到的数据」环形缓冲</b>（2026-10-13）。
///
/// <para><b>它要回答的问题只有一个</b>：刚才那一下，这台机器到底收到了什么？</para>
///
/// <para><b>为什么不落盘</b>：它是排查工具而不是审计日志。落盘会立刻带来
/// "这文件能长多大""怎么轮转""要不要清理"三个新问题，而这三件事对"刚刚发生了什么"
/// 毫无帮助。进程一停就没了，正是它该有的寿命 —— 要留证据的地方是客户机日志
/// （<see cref="LogService"/>），那里本来就有。</para>
///
/// <para>🔒 <b>线程安全</b>：写入方有两条互不相干的线程 —— UDP 收包循环与 SignalR 回调；
/// 读取方还有本机页面的 HTTP 线程。全部经同一把锁，临界区只做"入队 / 拷一份出来"，
/// 绝不在这里做 IO。</para>
/// </summary>
public sealed class ReceivedDataLog
{
    /// <summary>
    /// 环形缓冲容量。
    /// <para>
    /// 200 条：现场"我点了一下，没反应"的排查里，最近几十条足够了；
    /// 而一次灌包可能是每秒上万条，容量再大也只是多留一点垃圾。
    /// </para>
    /// </summary>
    public const int Capacity = 200;

    /// <summary>
    /// 单条 <see cref="AgentReceivedEntryDto.Raw"/> 的字符上限（200）。
    /// <para>
    /// 必须截断：UDP 单包能塞 64 KB，一个坏掉的发送方能把 200 条 × 64 KB ≈ 12 MB
    /// 挂在客户机内存里，再原样发给中心 —— 那是一次自伤式的拒绝服务。
    /// </para>
    /// </summary>
    public const int RawMaxLength = 200;

    private readonly object _gate = new();
    private readonly LinkedList<AgentReceivedEntryDto> _items = new();
    private readonly DateTimeOffset _startedAt = BeijingTime.Now;

    private long _received;

    /// <summary>本进程启动的时刻。</summary>
    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>本进程启动以来收到的报文总数（含已被挤出环形缓冲的）。</summary>
    public long ReceivedCount
    {
        get
        {
            lock (_gate) return _received;
        }
    }

    /// <summary>
    /// 记一条。
    /// <para>
    /// <b>绝不抛</b>：调用它的地方是收包循环与 Hub 回调，为了让"记录"失败而中断
    /// "处理指令"是本末倒置的。
    /// </para>
    /// </summary>
    public void Add(string kind, string source, string? raw, string outcome, string? detail = null)
    {
        try
        {
            var entry = new AgentReceivedEntryDto(
                BeijingTime.Now,
                kind,
                Truncate(source, RawMaxLength) ?? string.Empty,
                Truncate(raw, RawMaxLength) ?? string.Empty,
                outcome,
                Truncate(detail, 500));

            lock (_gate)
            {
                _received++;

                _items.AddFirst(entry);

                while (_items.Count > Capacity) _items.RemoveLast();
            }
        }
        catch
        {
            // 见方法注释：记录失败绝不能影响处理。
        }
    }

    /// <summary>只记"收到了一条数据报"的计数，不留条目（丢弃路径用）。</summary>
    public void CountOnly()
    {
        lock (_gate) _received++;
    }

    /// <summary>
    /// 取一份快照（新的在前）。
    /// <para>
    /// 把 <paramref name="udpStateText"/> / <paramref name="udpPort"/> 等通道状态一起打包，
    /// 是因为"一条都没收到"时真正有用的是那几项 —— 空的列表配上"UDP 通道未启动：…"
    /// 才是一句能读懂的话。
    /// </para>
    /// </summary>
    public AgentReceivedDataDto Snapshot(
        bool udpListening,
        int? udpPort,
        string? udpStateText,
        bool udpExecuteEnabled)
    {
        AgentReceivedEntryDto[] entries;
        long total;

        lock (_gate)
        {
            entries = _items.ToArray();
            total = _received;
        }

        return new AgentReceivedDataDto(
            Ok: true,
            Status: AgentReceivedStatuses.Ok,
            Message: null,
            UdpListening: udpListening,
            UdpPort: udpPort,
            UdpStateText: udpStateText,
            UdpExecuteEnabled: udpExecuteEnabled,
            Entries: entries,
            ReceivedCount: total,
            StartedAt: _startedAt);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= max) return value;

        return value[..max] + "…";
    }
}
