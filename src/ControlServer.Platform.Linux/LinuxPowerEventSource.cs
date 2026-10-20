using System.Diagnostics;
using System.Globalization;
using ControlServer.Contracts;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Linux;

/// <summary>
/// Linux 上的开关机记录（M7.2）：读 <c>wtmp</c>（命令 <c>last -x</c>）。
///
/// <para>
/// 🔥 <b>为什么选 <c>last</c> 而不是 <c>journalctl</c></b>：
/// journal 里"关机"与"崩溃"都表现为"这一条之后就没有了"，<b>判不出区别</b>；
/// 而 wtmp 是 <c>systemd-update-utmp</c> 在关机/重启时**主动写**的一条记录
/// （<c>shutdown</c> / <c>reboot</c> / <c>runlevel</c>），干净关机与断电在数据上就是两件事。
/// 这正好对应 <see cref="AgentPowerEventKinds.Shutdown"/> 与
/// <see cref="AgentPowerEventKinds.Unexpected"/> 的差别 —— 拿 journal 猜，
/// 只会把"每次断电"都报成"正常关机"，而那是这张表上最要紧的一栏。
/// </para>
///
/// <para>
/// ⚠️ <b>它需要读 <c>/var/log/wtmp</c></b>（通常 root 或 <c>utmp</c> 组可读）。
/// 读不到时 <see cref="IsAvailable"/> 为 <c>false</c>，页面上显示"当前平台/权限不支持"，
/// <b>而不是一张空表</b> —— 空表意味着"这台机器从来没关过机"，那是个截然不同的结论。
/// </para>
///
/// <para>
/// ⚠️ <b>没有"记录号"这回事</b>：wtmp 是定长记录的顺序文件，没有 Windows 那种
/// 单调递增的 EventRecordID。所以这里用<b>事件时刻的 Unix 秒</b>充当水位
/// （见 <see cref="HostPowerEventReading.RecordId"/> 的说明里那句"记录号单调"——
/// 开机与关机不可能落在同一秒，所以它在本用途上是单调且唯一的）。
/// 日志被轮转后水位会"倒退"，而那正是服务端 (AgentId, At, EventId) 去重兜底要处理的情形。
/// </para>
/// </summary>
public sealed class LinuxPowerEventSource : IPowerEventSource
{
    /// <summary>一次最多解析多少行（<c>last</c> 的输出是整份 wtmp，机器跑久了会很长）。</summary>
    private const int MaxLines = 400;

    private const int CommandTimeoutMs = 5000;

    private readonly string? _lastPath;

    public LinuxPowerEventSource()
    {
        _lastPath = FindExecutable("last");

        if (_lastPath is null)
        {
            AvailabilityNote = "本机没有 `last` 命令（sysvinit-tools / util-linux 未安装）—— 读不到开关机记录。";
            IsAvailable = false;
            return;
        }

        // 🔥 只探一次：真正的判据是"能不能读到 wtmp"，而它取决于权限。
        //    ⚠️ 不能在 ctor 里跑一次完整解析 —— 那会把进程启动拖慢几百毫秒，
        //       而这里只是要回答"这条路通不通"。
        var probe = Run(_lastPath, "-x -n 1");

        IsAvailable = probe.ExitCode == 0;

        AvailabilityNote = IsAvailable
            ? string.Empty
            : $"`last -x` 读不到 /var/log/wtmp（通常是权限：需要 root 或 utmp 组）。原始输出：{FirstLine(probe.Output)}";
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <summary>不可用的原因（可用时为空串）。</summary>
    public string AvailabilityNote { get; }

    /// <inheritdoc />
    public HostPowerEventBatch Read(DateTimeOffset from, long? afterRecordId)
    {
        if (!IsAvailable || _lastPath is null) return HostPowerEventBatch.Empty;

        var run = Run(_lastPath, $"-x -F --time-format=iso -n {MaxLines}");

        if (run.ExitCode != 0) return HostPowerEventBatch.Empty;

        var events = new List<HostPowerEventReading>();
        long? maxRecordId = afterRecordId;

        foreach (var line in run.Output.Split('\n'))
        {
            if (!TryParseLastLine(line, out var reading)) continue;

            // ⚠️ 两个条件都要（与接口注释同一条理由）：时间回溯 + 记录号水位。
            if (reading.At < from) continue;
            if (afterRecordId is { } watermark && (reading.RecordId ?? 0) <= watermark) continue;

            events.Add(reading);

            if (reading.RecordId is { } id && (maxRecordId is null || id > maxRecordId)) maxRecordId = id;
        }

        events.Sort((a, b) => a.At.CompareTo(b.At));

        return new HostPowerEventBatch(events, maxRecordId);
    }

    /// <summary>
    /// 解析 <c>last -x --time-format=iso</c> 的一行。
    ///
    /// <para>样例（列宽随用户名与内核名变化，所以<b>按关键词 + ISO 时间戳定位</b>，不按列号）：</para>
    /// <code>
    /// reboot   system boot  6.8.0-45-generic 2026-10-15T21:28:03+08:00   still running
    /// shutdown system down  6.8.0-45-generic 2026-10-15T21:30:03+08:00 - 2026-10-15T21:30:05+08:00  (00:00)
    /// </code>
    ///
    /// <para>
    /// ⚠️ <b>只认"system down/boot"这几种</b>：<c>last -x</c> 里还有 <c>runlevel</c> 之类的记录，
    /// 把它们都当成开关机会让一张表翻倍，而每条都长得像真的。
    /// </para>
    /// </summary>
    internal static bool TryParseLastLine(string? line, out HostPowerEventReading reading)
    {
        reading = null!;

        if (string.IsNullOrWhiteSpace(line)) return false;

        var text = line.Trim();

        // 表头 / 空 wtmp 提示（`wtmp begins ...`）。
        if (text.StartsWith("wtmp begins", StringComparison.Ordinal)) return false;

        string kind;

        if (text.Contains("system boot", StringComparison.Ordinal)) kind = AgentPowerEventKinds.Boot;
        else if (text.Contains("system down", StringComparison.Ordinal))
        {
            // ⚠️ `shutdown` 与 `reboot` 都是 "system down"，靠第一列区分。
            kind = text.StartsWith("reboot", StringComparison.Ordinal)
                ? AgentPowerEventKinds.Restart
                : AgentPowerEventKinds.Shutdown;
        }
        else if (text.Contains("system crash", StringComparison.Ordinal)
                 || text.StartsWith("crash", StringComparison.Ordinal))
        {
            // 🔥 断电 / 硬复位：wtmp 里写的是 crash（或干脆什么都没有）。
            //    它与"正常关机"是这张表上最要紧的区别。
            kind = AgentPowerEventKinds.Unexpected;
        }
        else
        {
            return false;
        }

        // 取**这一行里第一个能解析的 ISO 时间**：它就是这次事件发生的时刻
        // （后面的那个是"这一条记录被关闭/下一条开始"的时刻，与本次事件无关）。
        DateTimeOffset? at = null;

        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.Contains('T', StringComparison.Ordinal)) continue;
            if (!token.Contains('-', StringComparison.Ordinal)) continue;

            if (DateTimeOffset.TryParse(
                    token,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                at = parsed;
                break;
            }
        }

        if (at is not { } moment) return false;

        // RecordId 用 Unix 秒（见类注释：wtmp 没有记录号，而这两类事件不会同秒）。
        var recordId = moment.ToUnixTimeSeconds();

        reading = new HostPowerEventReading(
            moment,
            kind,
            EventId: null,
            RecordId: recordId,

            // ⚠️ wtmp 不记"谁发起的"（那是 journal 里才有的信息，而这条路上不读 journal）。
            //    编一个出来比留空更坏：留空至少让人去翻 journal。
            Actor: null,
            Detail: text);

        return true;
    }

    private static string FindExecutable(string name)
    {
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" })
        {
            var path = Path.Combine(dir, name);

            if (File.Exists(path)) return path;
        }

        return null!;
    }

    private static (int ExitCode, string Output) Run(string exe, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start()) return (-1, "进程没能启动。");

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 杀不掉也只能放弃 —— 但下面那句话要让现场知道"这次是超时"。
                }

                return (-1, $"`{exe} {arguments}` 超过 {CommandTimeoutMs} 毫秒没有结束。");
            }

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, $"{ex.Message}");
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return string.IsNullOrWhiteSpace(line) ? "（没有输出）" : line.Trim();
    }
}
