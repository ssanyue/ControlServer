using System.Globalization;
using System.Text;

namespace ControlServer.LoadTest;

/// <summary>
/// 把一轮压测写成 markdown 报告 + 一条 CSV 时间序列。
///
/// <para>
/// 🔥 <b>为什么不打印一句"通过/不通过"</b>：M6 的口径是"<b>先测曲线、再定门槛</b>"。
/// 工具如果自带一个门槛，就会变成"凑数据让灯变绿"。所以这里只给<b>事实</b>
/// （分位数、失败数、CPU、内存走势）与<b>基于事实的读法</b>，门槛由人拿着这份报告去定。
/// </para>
/// </summary>
public static class Report
{
    public static (string MarkdownPath, string CsvPath) Write(
        LoadTestOptions options,
        Summary summary,
        ServerProbe? probe,
        StreamProbe? stream,
        string serverVersion,
        TimeSpan elapsed)
    {
        Directory.CreateDirectory(options.OutDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = $"loadtest-{options.Tag}-{stamp}";

        var mdPath = Path.Combine(options.OutDirectory, baseName + ".md");
        var csvPath = Path.Combine(options.OutDirectory, baseName + ".csv");

        File.WriteAllText(csvPath, BuildCsv(summary), new UTF8Encoding(false));
        File.WriteAllText(mdPath, BuildMarkdown(options, summary, probe, stream, serverVersion, elapsed), new UTF8Encoding(false));

        return (mdPath, csvPath);
    }

    private static string BuildCsv(Summary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("second,connected,heartbeat_ok,heartbeat_fail,metrics_ok,metrics_fail,reconnects,commands_received,cpu_pct,working_set_mb,threads");

        foreach (var (second, data) in summary.Buckets)
        {
            sb.Append(second).Append(',')
              .Append(data.Connected).Append(',')
              .Append(data.HeartbeatOk).Append(',')
              .Append(data.HeartbeatFail).Append(',')
              .Append(data.MetricsOk).Append(',')
              .Append(data.MetricsFail).Append(',')
              .Append(data.Reconnects).Append(',')
              .Append(data.CommandsReceived).Append(',')
              .Append(N(data.ServerCpuPct)).Append(',')
              .Append(N(data.ServerWorkingSetMb)).Append(',')
              .Append(data.ServerThreads == 0 ? string.Empty : data.ServerThreads.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildMarkdown(
        LoadTestOptions options,
        Summary summary,
        ServerProbe? probe,
        StreamProbe? stream,
        string serverVersion,
        TimeSpan elapsed)
    {
        var sb = new StringBuilder();

        var cpu = summary.Buckets
            .Select(b => b.Data.ServerCpuPct)
            .Where(v => !double.IsNaN(v))
            .ToArray();

        var ws = summary.Buckets
            .Select(b => b.Data.ServerWorkingSetMb)
            .Where(v => !double.IsNaN(v))
            .ToArray();

        var maxConnected = summary.Buckets.Count == 0
            ? 0
            : summary.Buckets.Max(b => b.Data.Connected);

        var heartbeatTotal = summary.HeartbeatOk + summary.HeartbeatFail;
        var heartbeatFailPct = heartbeatTotal == 0
            ? 0
            : summary.HeartbeatFail * 100.0 / heartbeatTotal;

        // ⚠️ 均值不能用百分位函数取名 0% —— 那是**最小值**，会永远显示 0.0%。
        var avgCpu = cpu.Length == 0 ? double.NaN : cpu.Average();

        sb.AppendLine($"# 压测报告 · {options.Tag}");
        sb.AppendLine();
        sb.AppendLine($"> 生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　时长 {elapsed.TotalMinutes:0.0} 分钟"
                      + $"　|　中心 {options.Server}　|　服务端版本 {serverVersion}");
        sb.AppendLine();

        // ── 结论先行 ──
        sb.AppendLine("## 0. 先说结论（自动读数，不含门槛判断）");
        sb.AppendLine();
        sb.AppendLine("| 问题 | 这一轮的答案 |");
        sb.AppendLine("|---|---|");

        sb.AppendLine($"| {options.Agents} 台都连上了吗？ | 注册成功 {summary.RegisterOk} / 失败 {summary.RegisterFail}；"
                      + $"峰值在线 {maxConnected} 台（假机器自报，见 §6）|");

        sb.AppendLine($"| 心跳稳不稳？ | 成功 {summary.HeartbeatOk} / 失败 {summary.HeartbeatFail}"
                      + $"（失败率 {heartbeatFailPct:0.00}%），断线重连 {summary.Reconnects} 次 |");

        sb.AppendLine($"| 心跳有多慢？ | P50 {Ms(summary.HeartbeatRtt, 0.50)}、P95 {Ms(summary.HeartbeatRtt, 0.95)}、"
                      + $"P99 {Ms(summary.HeartbeatRtt, 0.99)} |");

        sb.AppendLine($"| 中心吃多少 CPU？ | 峰值 {Pct(cpu, 1.0)}、均值 {Num(avgCpu, "%")}（占整机） |");

        sb.AppendLine($"| 内存有没有一路涨？ | 峰值 {Mb(ws, 1.0)}、末段均值 {Mb(Tail(ws, 0.1), 0.0)}"
                      + $"（与首段 {Mb(Head(ws, 0.1), 0.0)} 比：{Drift(ws)}） |");

        if (options.CommandsPerMinute > 0)
        {
            var lost = summary.CommandsSent - summary.CommandsReceived;
            sb.AppendLine($"| 命令丢没丢？ | 中心下发成功 {summary.CommandsSent} 条，"
                          + $"假机器实收 {summary.CommandsReceived} 条，差 {lost} 条 |");
        }

        if (options.Streams > 0)
        {
            var arrival = summary.FramesSent == 0
                ? 0
                : summary.FramesReceived * 100.0 / summary.FramesSent;

            sb.AppendLine($"| 推流被饿死了吗？ | 发出 {summary.FramesSent} 帧、观看端收到 {summary.FramesReceived} 帧"
                          + $"（到达率 {arrival:0.0}%），端到端 P95 {Ms(summary.FrameLatency, 0.95)} |");
        }

        sb.AppendLine();

        // ── 条件 ──
        sb.AppendLine("## 1. 这一轮的条件");
        sb.AppendLine();
        sb.AppendLine("| 项 | 值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 规模 | {options.Agents} 台 × {options.Minutes:0.##} 分钟，爬坡 {options.RampSeconds:0.#} s |");
        sb.AppendLine($"| 心跳间隔 | {options.HeartbeatSeconds?.ToString() ?? "服务端下发"} |");
        sb.AppendLine($"| 遥测间隔 | {options.MetricsSeconds?.ToString() ?? "服务端下发"} |");
        sb.AppendLine($"| L2 命令 | {(options.CommandsPerMinute > 0 ? $"{options.CommandsPerMinute} 条/分钟" : "未测")} |");
        sb.AppendLine($"| L3 推流 | {(options.Streams > 0 ? $"{stream?.ActiveStreams ?? 0} 路，{options.StreamFrameBytes / 1024} KB × {options.StreamFps} fps" : "未测")} |");
        sb.AppendLine($"| 中心进程 | {(probe is null ? "未采集" : $"{probe.ProcessName}（CPU/内存按进程采）")} |");
        // 硬件基线必须写进报告：**门槛是跟着机器走的**，换个 4 核机器这些数字全变。
        // 内存总量取 GC 能看到的可用量（容器里就是配额，比 WMI 更贴近进程实际能用的）。
        var totalMemGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;

        sb.AppendLine($"| 本机 | {Environment.ProcessorCount} 逻辑核 / {totalMemGb:0.0} GB 内存 / "
                      + $"{Environment.OSVersion.VersionString} / .NET {Environment.Version} |");
        sb.AppendLine();

        // ── 明细 ──
        sb.AppendLine("## 2. 明细读数");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 样本数 | P50 | P95 | P99 | 最大 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");

        sb.AppendLine($"| 心跳 RTT（ms） | {summary.HeartbeatRtt.Length} | {Ms(summary.HeartbeatRtt, 0.50)} | "
                      + $"{Ms(summary.HeartbeatRtt, 0.95)} | {Ms(summary.HeartbeatRtt, 0.99)} | {Ms(summary.HeartbeatRtt, 1.0)} |");

        sb.AppendLine($"| 遥测 RTT（ms） | {summary.MetricsRtt.Length} | {Ms(summary.MetricsRtt, 0.50)} | "
                      + $"{Ms(summary.MetricsRtt, 0.95)} | {Ms(summary.MetricsRtt, 0.99)} | {Ms(summary.MetricsRtt, 1.0)} |");

        if (summary.CommandRtt.Length > 0)
        {
            sb.AppendLine($"| 命令下发 HTTP（ms） | {summary.CommandRtt.Length} | {Ms(summary.CommandRtt, 0.50)} | "
                          + $"{Ms(summary.CommandRtt, 0.95)} | {Ms(summary.CommandRtt, 0.99)} | {Ms(summary.CommandRtt, 1.0)} |");
        }

        if (summary.FrameLatency.Length > 0)
        {
            sb.AppendLine($"| 推流端到端（ms） | {summary.FrameLatency.Length} | {Ms(summary.FrameLatency, 0.50)} | "
                          + $"{Ms(summary.FrameLatency, 0.95)} | {Ms(summary.FrameLatency, 0.99)} | {Ms(summary.FrameLatency, 1.0)} |");
        }

        sb.AppendLine();
        sb.AppendLine($"中心进程：CPU 峰值 **{Pct(cpu, 1.0)}** / 均值 **{Num(avgCpu, "%")}**（占整机），"
                      + $"峰值内存 **{Mb(ws, 1.0)}**，线程峰值 "
                      + $"{summary.Buckets.Select(b => b.Data.ServerThreads).DefaultIfEmpty(0).Max()}。");
        sb.AppendLine();

        // ── 时间序列（按 10 秒窗口聚合） ──
        //
        // 🔥 **必须是"窗口求和"，不能是"第 10、20、30 秒那一秒的值"**：
        //    心跳每 10 秒一次、遥测每 5 秒一次，采样点大多落在窗口中间的某一秒；
        //    只看整十秒会读到一堆 0，让人误以为"10 秒才 4 次心跳"。
        sb.AppendLine("## 3. 时间序列（每 10 秒一个窗口；完整逐秒数据见同名 .csv）");
        sb.AppendLine();
        sb.AppendLine("| t(s) | 在线（窗口峰值） | 心跳 ok | 心跳 fail | 遥测 ok | 重连 | 命令收到 | 中心 CPU%（窗口均） | 内存 MB（窗口均） |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        var windows = summary.Buckets
            .GroupBy(b => b.Second / 10)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                T = g.Key * 10,
                Connected = g.Max(x => x.Data.Connected),
                HbOk = g.Sum(x => x.Data.HeartbeatOk),
                HbFail = g.Sum(x => x.Data.HeartbeatFail),
                MtOk = g.Sum(x => x.Data.MetricsOk),
                Reconnects = g.Sum(x => x.Data.Reconnects),
                Commands = g.Sum(x => x.Data.CommandsReceived),
                Cpu = Average(g.Select(x => x.Data.ServerCpuPct)),
                Ws = Average(g.Select(x => x.Data.ServerWorkingSetMb)),
            });

        foreach (var window in windows)
        {
            sb.AppendLine($"| {window.T} | {window.Connected} | {window.HbOk} | {window.HbFail} | "
                          + $"{window.MtOk} | {window.Reconnects} | {window.Commands} | "
                          + $"{Num(window.Cpu, string.Empty)} | {Num(window.Ws, string.Empty)} |");
        }

        sb.AppendLine();

        // ── L2 / L3 ──
        if (options.CommandsPerMinute > 0)
        {
            sb.AppendLine("## 4. L2 命令下发");
            sb.AppendLine();
            sb.AppendLine($"- 中心侧下发成功 **{summary.CommandsSent}** 条，失败 **{summary.CommandsSentFail}** 条；");
            sb.AppendLine($"- 假机器实收 **{summary.CommandsReceived}** 条，"
                          + $"差 **{summary.CommandsSent - summary.CommandsReceived}** 条（下发成功但没到）。");
            if (summary.CommandsReceivedOther > 0)
            {
                sb.AppendLine($"- 另有 **{summary.CommandsReceivedOther}** 条走同一通道的非 L2 指令（建流 / 停流 / 改配置），"
                              + "已**排除在漏命令判定之外** —— 混进来会得出\"收到比发出多\"的假读数。");
            }
            sb.AppendLine();
            sb.AppendLine("> 🔴 **协议现状**：`/hubs/agent` 的 `Command` 是**单向推送、没有回执**。"
                          + "中心回 200 只代表\"已经交给长连接\"，不代表对方收到、更不代表执行了 —— "
                          + "`AgentsController` 的返回语就写着\"指令是否真的执行，请看那台机器上的『接收到的数据』\"。"
                          + "所以这里用\"假机器实收几条\"来量，而不是去读一个根本不存在的回执。");
            sb.AppendLine();
        }

        if (options.Streams > 0 && stream is not null)
        {
            sb.AppendLine("## 5. L3 远控推流");
            sb.AppendLine();
            sb.AppendLine($"- 实际跑起来 **{stream.ActiveStreams}** 路；");
            sb.AppendLine($"- 发出 {summary.FramesSent} 帧（失败 {summary.FramesSendFail}），观看端收到 {summary.FramesReceived} 帧；");
            sb.AppendLine($"- 端到端 P50 {Ms(summary.FrameLatency, 0.50)}、P95 {Ms(summary.FrameLatency, 0.95)}；");
            sb.AppendLine();

            if (stream.Issues.Count > 0)
            {
                sb.AppendLine("建流过程中遇到的问题（不影响其它统计，但会影响 L3 结论）：");
                sb.AppendLine();

                foreach (var issue in stream.Issues)
                {
                    sb.AppendLine($"- {issue}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("> ⚠️ 中心那条转发队列是**容量 1 + 丢最旧**（设计如此）⇒ "
                          + "\"收到 < 发出\"是正常形态，看的是**到达率**与延迟，不是零丢帧。");
            sb.AppendLine();
        }

        // ── 边界 ──
        sb.AppendLine("## 6. 这份报告不能说明什么");
        sb.AppendLine();
        sb.AppendLine("- 🔴 **测的是中心，不是被控端。** 假 Agent 不抓屏、不编码、不推真码流 ⇒ "
                      + "**不能**用它承诺\"100 台 4K 远控\"。真机每台还要各自跑抓屏与硬编（本机物理不可得）。");
        sb.AppendLine("- 帧是随机字节：中心不解码只转发，对中心的压力真实，但**测不到浏览器解码**的开销。");
        sb.AppendLine("- 没有采集 GC 次数 / GC 暂停 / 线程池队列长度（要 `dotnet-counters`，本工具不带）。");
        sb.AppendLine("- 所有假机器都注册在目标实例的库里。**必须用临时数据目录起的实例**，"
                      + "跑完把那个目录删掉 —— 否则在线表里会留下 N 台不存在的机器。");
        sb.AppendLine("- 🔴 **「峰值在线」是假机器自报的（客户端视角），不是中心侧的连接表。**"
                      + " 中心进程被强杀时 TCP 不会立刻断，客户端要连败 3 次心跳才判定断线；"
                      + "那 20–30 秒里报告仍显示满员，而中心其实已经什么都没有了。"
                      + " 要量\"中心认为几台在线\"必须查中心的表，本工具不替代它。");
        sb.AppendLine("- 🔴 **短于 5 分钟的轮次，别信\"内存有没有一路涨\"这一行。**"
                      + " 这台机器光启动预热（JIT + 首次迁移 + N 台同时握手）就要 60–90 秒，"
                      + "前 20% 的热身段不够长 ⇒ 会把\"还在爬坡\"读成\"涨了 X%\"（实测 3 分钟那轮报了 +12.1%，"
                      + "而同配置跑满 30 分钟是 −5.8%）。");
        sb.AppendLine("- CPU/内存按**启动时那个 PID** 采。若中心中途重启（升级演练），后面几段会是 `—`——"
                      + "那是\"采不到\"，不是\"占用 0\"；改采新 PID 需要重开一轮。");
        sb.AppendLine("- Linux / Docker 侧未在本轮覆盖（沿用既有挂账）。");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string N(double value) =>
        double.IsNaN(value) ? "—" : value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Num(double value, string suffix) =>
        double.IsNaN(value) ? "—" : value.ToString("0.0", CultureInfo.InvariantCulture) + suffix;

    /// <summary>窗口均值：跳过采样失败的秒（NaN），别把"没采到"算成 0。</summary>
    private static double Average(IEnumerable<double> values)
    {
        var list = values.Where(v => !double.IsNaN(v)).ToArray();
        return list.Length == 0 ? double.NaN : list.Average();
    }

    private static string Ms(double[] values, double percentile)
    {
        if (values.Length == 0) return "—";

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)].ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string Pct(double[] values, double percentile)
    {
        if (values.Length == 0) return "—";

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)].ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Mb(double[] values, double percentile)
    {
        if (values.Length == 0) return "—";

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)].ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    private static double[] Head(double[] values, double fraction) =>
        values.Length == 0 ? Array.Empty<double>() : values.Take(Math.Max(1, (int)(values.Length * fraction))).ToArray();

    private static double[] Tail(double[] values, double fraction) =>
        values.Length == 0 ? Array.Empty<double>() : values.Skip(Math.Max(0, values.Length - Math.Max(1, (int)(values.Length * fraction)))).ToArray();

    /// <summary>
    /// 内存走势：末段比中段高多少 —— 压测里最常见的隐患是"缓慢上涨"而不是"一上来就爆"。
    ///
    /// <para>
    /// 🔥 <b>必须跳过前 20%</b>：那段包含 JIT、连接建立、SQLite 首次迁移的一次性开销。
    /// 拿它当基线会把"启动预热"误报成"内存在漏" —— 20 台那轮就报了 +38.9%，
    /// 而实际末段已经比峰值回落了。
    /// </para>
    /// </summary>
    private static string Drift(double[] values)
    {
        if (values.Length < 20) return "样本太少，不下结论";

        var warm = values.Skip(values.Length / 5).ToArray();

        if (warm.Length < 10) return "样本太少，不下结论";

        var head = Head(warm, 0.25).Average();
        var tail = Tail(warm, 0.25).Average();
        var delta = tail - head;
        var pct = head <= 0 ? 0 : delta / head * 100;

        return delta <= 8
            ? $"基本持平（{delta:+0.0;-0.0;0} MB，已跳过热身段）"
            : $"上涨 {delta:0.0} MB（{pct:0.0}%，已跳过热身段）—— 要盯住";
    }
}
