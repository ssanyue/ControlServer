using System.Globalization;

namespace ControlServer.LoadTest;

/// <summary>
/// 压测参数。
///
/// <para>
/// 🔥 <b>没有默认的 <c>--server</c></b>：本工具会往目标中心 <b>注册上百台假机器</b>，
/// 一旦默认值指向了现场那台正在跑的中心，就是往生产库里灌 100 条垃圾记录。
/// 所以目标必须由人显式写出来 —— 少打一个参数就退出，比"跑了 5 分钟才发现打错了"便宜得多。
/// </para>
/// </summary>
public sealed class LoadTestOptions
{
    /// <summary>中心地址（必填，例：<c>http://127.0.0.1:21801</c>）。</summary>
    public required string Server { get; init; }

    /// <summary>假 Agent 台数。</summary>
    public int Agents { get; init; } = 20;

    /// <summary>压测时长（分钟）。</summary>
    public double Minutes { get; init; } = 5;

    /// <summary>爬坡秒数：N 台在这么多秒内均匀连上来。</summary>
    public double RampSeconds { get; init; } = 5;

    /// <summary>每台每秒发几次心跳；<c>null</c> = 用服务端 Welcome 下发的间隔（生产口径）。</summary>
    public int? HeartbeatSeconds { get; init; }

    /// <summary>每台每隔几秒发一批遥测；<c>null</c> = 用服务端下发的间隔。</summary>
    public int? MetricsSeconds { get; init; }

    /// <summary>每分钟从中心往假机器下发多少条命令（0 = 不测 L2）。</summary>
    public int CommandsPerMinute { get; init; }

    /// <summary>同时跑几路假推流（0 = 不测 L3；方案定的是 1–2 路）。</summary>
    public int Streams { get; init; }

    /// <summary>假帧大小（字节）。</summary>
    public int StreamFrameBytes { get; init; } = 32 * 1024;

    /// <summary>假推流帧率。</summary>
    public int StreamFps { get; init; } = 30;

    /// <summary>登录管理端点用的账号。</summary>
    public string User { get; init; } = "admin";

    /// <summary>登录口令。</summary>
    public string Password { get; init; } = "admin123";

    /// <summary>
    /// 报告默认目录：<c>&lt;本工具源码目录&gt;/reports</c>。
    ///
    /// <para>
    /// 🔴 <b>2026-10-17 补测重连时踩出来的</b>：原来默认值就是相对路径 <c>"reports"</c>，
    /// 而相对路径是<b>相对当前工作目录</b>的 —— 从仓库根敲一次命令，报告就落到了
    /// <c>D:\Project\zhongkong\reports\</c>；从 <c>deploy/loadtest</c> 敲又落到另一个地方。
    /// 同一批报告分成两堆，<c>.gitignore</c> 也得跟着猜。
    /// </para>
    /// <para>
    /// 现在按<b>程序集位置</b>上溯找 csproj 目录（<c>bin/Debug/net8.0 → … → ControlServer.LoadTest</c>），
    /// 报告固定落在工具自己的 <c>reports/</c> 下，与从哪里敲命令无关。
    /// 找不到（例如 exe 被单独拷走）才退回相对 cwd —— 那种情况下 cwd 是唯一线索。
    /// </para>
    /// </summary>
    public static string DefaultOutDirectory { get; } = ResolveDefaultOutDirectory();

    private static string ResolveDefaultOutDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (string.Equals(dir.Name, "ControlServer.LoadTest", StringComparison.OrdinalIgnoreCase))
            {
                // ⚠️ 落到**上一级**（deploy/loadtest/reports），不是工具自己的目录：
                //    报告属于"这一套压测脚本"的产出，和 run-loadtest.ps1/.sh、README 放一起；
                //    塞进 csproj 目录会在 `dotnet build` 时混进 bin/obj 旁边，也很难找。
                var parent = dir.Parent ?? dir;
                return Path.Combine(parent.FullName, "reports");
            }

            dir = dir.Parent;
        }

        return "reports";
    }

    /// <summary>报告输出目录（默认落在本工具源码目录下的 <c>reports/</c>，见 <see cref="DefaultOutDirectory"/>）。</summary>
    public string OutDirectory { get; init; } = DefaultOutDirectory;

    /// <summary>报告文件名标签（例：<c>100</c>）。</summary>
    public string Tag { get; init; } = "run";

    /// <summary>中心进程 PID（用于采 CPU/内存；0 = 按进程名找）。</summary>
    public int ServerPid { get; init; }

    /// <summary>
    /// 解析失败时返回 <c>null</c>（调用方打用法并退 2）。
    /// </summary>
    public static LoadTestOptions? Parse(string[] args)
    {
        string? server = null;
        var agents = 20;
        var minutes = 5.0;
        var ramp = 5.0;
        int? heartbeat = null;
        int? metrics = null;
        var commands = 0;
        var streams = 0;
        var frameBytes = 32 * 1024;
        var fps = 30;
        var user = "admin";
        var password = "admin123";
        var outDir = DefaultOutDirectory;
        var tag = "run";
        var pid = 0;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{args[i]} 后面缺一个值。");

            switch (args[i])
            {
                case "--server": server = Next(); break;
                case "--agents": agents = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--minutes": minutes = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--ramp": ramp = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--heartbeat": heartbeat = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--metrics": metrics = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--commands-per-min": commands = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--streams": streams = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--stream-frame-kb": frameBytes = int.Parse(Next(), CultureInfo.InvariantCulture) * 1024; break;
                case "--stream-fps": fps = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--user": user = Next(); break;
                case "--password": password = Next(); break;
                case "--out": outDir = Next(); break;
                case "--tag": tag = Next(); break;
                case "--server-pid": pid = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--help":
                case "-h":
                    return null;
                default:
                    throw new FormatException($"不认识的参数：{args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(server)) return null;
        if (agents <= 0 || minutes <= 0) return null;

        return new LoadTestOptions
        {
            Server = server.TrimEnd('/'),
            Agents = agents,
            Minutes = minutes,
            RampSeconds = Math.Max(0, ramp),
            HeartbeatSeconds = heartbeat,
            MetricsSeconds = metrics,
            CommandsPerMinute = Math.Max(0, commands),
            Streams = Math.Clamp(streams, 0, 4),
            StreamFrameBytes = Math.Clamp(frameBytes, 1024, 4 * 1024 * 1024),
            StreamFps = Math.Clamp(fps, 1, 60),
            User = user,
            Password = password,
            OutDirectory = outDir,
            Tag = tag,
            ServerPid = pid,
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            压测工具（M6.3）—— 协议级假 Agent，测的是**中心**，不是被控端。

              --server <url>           中心地址（必填，例 http://127.0.0.1:21801）
              --agents <n>             假 Agent 台数（默认 20）
              --minutes <m>            时长（分钟，可带小数，默认 5）
              --ramp <s>               爬坡秒数（默认 5）
              --heartbeat <s>          心跳间隔（默认用服务端 Welcome 下发的值）
              --metrics <s>            遥测间隔（默认用服务端下发的值）
              --commands-per-min <k>   L2：每分钟下发多少条命令（默认 0 = 不测）
              --streams <n>            L3：几路假推流（默认 0 = 不测；方案定 1–2 路）
              --stream-frame-kb <kb>   假帧大小（默认 32 KB）
              --stream-fps <n>         假推流帧率（默认 30）
              --user / --password      管理端点账号（默认 admin / admin123）
              --server-pid <pid>       中心进程 PID（采 CPU/内存用；0 = 按进程名找）
              --out <dir>              报告目录（默认 <本工具目录>/reports，与 cwd 无关）
              --tag <name>             报告标签（默认 run）

            ⚠️ 会在目标中心**注册 N 台假机器**，只许对**临时数据目录起的实例**用。
            """);
    }
}
