using System.Diagnostics;
using System.Runtime.InteropServices;
using ControlServer.Contracts;
using ControlServer.Core.Abstractions;
using ControlServer.Core.Licensing;
using ControlServer.Core.Services;
using ControlServer.Infrastructure.Notifications;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Infrastructure.Storage;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Linux;
using ControlServer.Platform.Windows;
using ControlServer.Server;
using ControlServer.Server.Cli;
using ControlServer.Server.HostedServices;
using ControlServer.Server.Hubs;
using ControlServer.Server.Logging;
using ControlServer.Server.Options;
using ControlServer.Server.Security;
using ControlServer.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Serilog;
using Serilog.Events;

// ⚠️ Program.cs 同时 using 了 Serilog 与 Microsoft.Extensions.Logging，
//    裸写 ILogger 会 CS0104（两个同名接口）。这里给 MEL 的起个别名。
using MelLogger = Microsoft.Extensions.Logging.ILogger;

// ═══════════════════════════════════════════════════════════════
// 0. 全局异常兜底 —— 必须在一切之前注册。
//    后台异常绝不允许静默杀死进程（"一定要稳定"的第一条）。
// ═══════════════════════════════════════════════════════════════
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "【致命】未捕获的 AppDomain 异常");
    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Error(e.Exception, "【兜底】未观察的 Task 异常（已 SetObserved，避免进程被终止）");
    e.SetObserved();
};

// ═══════════════════════════════════════════════════════════════
// 0.1 宿主形态 A1（已拍板）：WinExe 的"控制台"处理
//
//     本 exe 是 **GUI 子系统**（csproj 里 OutputType=WinExe）⇒ 双击时**没有控制台窗口**
//     ⇒ 用户"关掉控制台"这个动作根本不存在，服务自然不会被它带走。
//     停服务由托盘图标右键菜单负责。
//
//     ⚠️ 但"从 cmd / run.cmd 启动"时进程会【继承】那个控制台，而 Win32 的规则是 ——
//        控制台窗口被关闭时向所有客户端进程发 CTRL_CLOSE_EVENT，且**应用无法否决**
//        （处理器返回 TRUE 也只能把死期从 0 秒拖到约 5 秒）。于是"关掉那扇窗 = 顺手
//        把服务也停了"，这正是需求明确不允许的行为。**唯一解是主动 FreeConsole() 脱离**。
//        真机复现证据：从 cmd 启动时"服务进程有控制台=True"，关窗后 1 秒内服务消失。
//
//     开发/排障时想在这个终端看实时日志：加 --Server:Startup:AttachConsole=true
//     （此时保留控制台，代价是关掉这个终端会同时停服务）。
//
//     ⚠️ 必须在 Serilog 之前 —— 返回值决定 Console sink 要不要挂。
// ═══════════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════
// 0.2 维护子命令（--backup / --restore）—— 必须在 CreateBuilder 之前分流
//
//     两条都是硬理由，不是风格问题：
//     ① CreateBuilder 会把 `--backup D:\bk` 里的裸目录当成非法命令行配置直接抛
//        （CommandLineConfigurationProvider 只认 --key=value / --key value）；
//     ② 真等建到一半再 return，Kestrel 已经绑过端口、Serilog 已经开过文件。
//
//     ⚠️ 维护命令还有第三条路：**根本不动控制台**。
//        最初写的是"给它 attach=true 保留控制台"（因为服务默认会 FreeConsole 脱离终端，
//        照搬会让运维看到一片空白）。结果在 ConPTY / 被父进程接管的场景下输出整段消失 ——
//        AttachConsole(父) 会把 stdout 句柄换成父进程那个，而那条管道并不是我们该写的地方。
//        维护命令本来就跑在用户的终端里，保持现状就是对的：既不 FreeConsole，也不 attach。
// ═══════════════════════════════════════════════════════════════
if (DataMaintenanceCommand.TryParse(args, out var maintenanceInvocation))
{
    Environment.ExitCode = DataMaintenanceCommand.Execute(maintenanceInvocation!);
    return;
}

var attachConsoleToParent = ReadAttachConsoleFlag(args);
var consoleOutputUsable = WindowsInteractiveHost.ConfigureConsoleOutput(attachConsoleToParent);

if (attachConsoleToParent && consoleOutputUsable && OperatingSystem.IsWindows())
{
    Console.WriteLine(
        "[控制台] 已保留父进程控制台（开发模式）：关闭此终端会同时停止服务。"
        + "正式运行请去掉 --Server:Startup:AttachConsole=true，日志见数据目录的 logs\\ 子目录。");
}

var builder = WebApplication.CreateBuilder(args);

// ═══════════════════════════════════════════════════════════════
// 0.5 便携配置：允许把 appsettings.json 放在【exe 同目录】。
//
//     为什么需要单独加这一步：默认配置源按「当前工作目录」找 appsettings.json。
//     双击运行时工作目录恰是 exe 所在目录（能命中），但注册成 Windows 服务后
//     工作目录是 System32，同目录的配置文件就【读不到】了。
//     这里显式按 exe 真实目录再挂一个源，两种形态行为一致。
//
//     插入位置在「环境变量」之前 —— 环境变量与命令行仍拥有最高优先级
//     （集成测试与容器化部署依赖这一点）。
// ═══════════════════════════════════════════════════════════════
var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
var portableConfigPath = string.IsNullOrEmpty(executableDirectory)
    ? null
    : Path.Combine(executableDirectory, "appsettings.json");

if (portableConfigPath is not null && File.Exists(portableConfigPath))
{
    InsertConfigSourceBeforeEnvironmentVariables(builder.Configuration, executableDirectory!, "appsettings.json");
}

// ═══════════════════════════════════════════════════════════════
// 1. 数据目录 —— 必须早于 DI，因为日志文件路径依赖它
// ═══════════════════════════════════════════════════════════════
var configuredDataDir = builder.Configuration["Server:Storage:DataDir"];
var dataRoot = string.IsNullOrWhiteSpace(configuredDataDir)
    ? DataDirectoryDefaults.ResolveDefaultRoot()
    : configuredDataDir!;

var dataDirectory = new FileSystemDataDirectory(dataRoot);

try
{
    dataDirectory.EnsureCreated();
}
catch (Exception ex)
{
    // 数据目录不可写 = 无法运行，直接明确失败（不裸崩）。
    Console.Error.WriteLine($"【启动失败】无法创建数据目录 {dataRoot}：{ex.Message}");
    Environment.ExitCode = 1;
    return;
}

// ═══════════════════════════════════════════════════════════════
// 1.5 端口覆盖配置（M2-E5）
//
//     监听端口的"热切换"要能跨重启保持，就必须有一个**可写**的来源。
//     不写 appsettings.json 的三个理由（部署目录常常只读 / 单文件发布时可能来自内嵌资源 /
//     会重排用户手写的注释与格式）见 ServerAddressOverrideStore 的类注释。
//
//     挂载位置约束（两条都不能破）：
//       · 排在 exe 同目录 appsettings.json **之后** —— 否则覆盖不了用户配置；
//       · 排在内置环境变量源**之前** —— 否则剥夺了"运维用环境变量兜底"的能力
//         （集成测试与容器化部署依赖环境变量拥有更高优先级）。
// ═══════════════════════════════════════════════════════════════
var addressOverrideStore = new ServerAddressOverrideStore(dataDirectory);
if (addressOverrideStore.Exists)
{
    InsertConfigSourceBeforeEnvironmentVariables(
        builder.Configuration,
        dataDirectory.Root,
        ServerAddressOverrideStore.FileName);
}

// ═══════════════════════════════════════════════════════════════
// 2. 日志（服务形态无控制台 → 必须落文件）
// ═══════════════════════════════════════════════════════════════
var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    // EF Core 默认把每条 SQL 都以 Information 打出来，会把 server-*.log 淹掉
    // （启动自检那几行要翻很久才找得到）。降到 Warning 只留真正的异常。
    // 需要看 SQL 时临时改成 Debug 即可。
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    // M2-D：把校正后的北京时间作为 {BeijingTime} 注入。
    // 模板里必须用它替代 {Timestamp}，否则日志内容永远吃不到 NTP 校正。
    .Enrich.With<BeijingTimeEnricher>();

// ⚠️ Console sink 按需挂：默认路径（从终端/cmd 启动）会**主动脱离控制台**
//    （见 §0.1），此时标准输出句柄已失效，挂了也是白写并可能让 Serilog 反复报错。
//    被重定向到文件/管道的场景返回 true（脱离不会关掉文件/管道句柄），日志照旧。
if (consoleOutputUsable)
{
    loggerConfiguration = loggerConfiguration.WriteTo.Console(
        outputTemplate: "[{BeijingTime:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
}

Log.Logger = loggerConfiguration
    .WriteTo.File(
        Path.Combine(dataDirectory.LogDirectory, "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        outputTemplate: "{BeijingTime:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

Log.Information("════════ 中控服务版 启动中 ════════");

// 版本必须落在启动自述里（M6.4）：升级/回滚/"现场跑的是不是刚发的那个包"全靠这一行对。
// 只有三段版本号时，升完级与回滚完打印出来一模一样 —— 那就等于没有版本。
Log.Information("程序版本：{Version}", ServerRuntimeInfo.DescribeVersion());
Log.Information("数据目录：{DataRoot}", dataDirectory.Root);
Log.Information("运行平台：{Os}", RuntimeInformation.OSDescription);

// ANSI 自检：中文编码在 Linux 上最易静默乱码（M2 方案 §13 风险缓解），
// 启动时明确打印当前实际生效的 ANSI 编码；拿不到 CP936 会在日志里直接标红。
Log.Information("{AnsiSelfCheck}", DataEncoder.DescribeAnsiEncoding());

if (portableConfigPath is not null && File.Exists(portableConfigPath))
{
    Log.Information("便携配置：已加载 {Path}", portableConfigPath);
}

if (addressOverrideStore.Exists)
{
    Log.Information(
        "端口覆盖配置：已加载 {Path}（监听地址 = {Urls}）",
        addressOverrideStore.FilePath,
        builder.Configuration["Server:Http:Urls"] ?? "(未设置，改用 Server:Http:Port)");
}

// ═══════════════════════════════════════════════════════════════
// 3. 宿主形态：Windows 服务 / systemd / 控制台（同一套代码）
// ═══════════════════════════════════════════════════════════════
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Host.UseWindowsService();
    Log.Information("宿主形态：Windows 服务");
}
else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
{
    builder.Host.UseSystemd();
    Log.Information("宿主形态：systemd");
}
else
{
    Log.Information("宿主形态：控制台");
}

// ═══════════════════════════════════════════════════════════════
// 4. Kestrel 监听地址
// ═══════════════════════════════════════════════════════════════
var urls = builder.Configuration["Server:Http:Urls"];
if (string.IsNullOrWhiteSpace(urls))
{
    var port = builder.Configuration.GetValue("Server:Http:Port", 8899);
    urls = $"http://0.0.0.0:{port}";
}

// ── HTTPS 端点（M5.3）──
//
// 🔥 **它不是一个"可选的加固"，而是观看端那条链路的硬前提**：
//    浏览器把 https 与 http://localhost 之外的一切判为非安全上下文，
//    而在非安全上下文里 VideoDecoder / AudioDecoder / AudioWorklet **压根不存在**
//    （Chrome 94 起重就是这样，141 复验）。观看端正是"局域网另一台电脑按 IP 访问"，
//    所以不加这一步，M5.3 的音频与 M5.4 的硬解在设计上就跑不起来 ——
//    而失败形态是"音频区域一句错都没有，就是永远没声音"，最难查的那一类。
//
// ⚠️ http 端口**必须保留**，两个理由：
//    ① 客户机 Agent 的长连接、UDP 发现、注册全都指着它（那些是 .NET 进程，不受这条限制）；
//    ② 根证书要从明文端口下载 —— 观看端还没信任证书时，https 那条路会先弹一道
//       "连接不安全"，而"看见警告点继续"这个习惯一旦养成，这套信任机制就白做了（见 TrustEndpoints）。
var httpsSetup = HttpsSetup.Resolve(builder.Configuration, urls);

if (httpsSetup.Enabled
    && !urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
{
    urls = $"{urls.TrimEnd(';', ' ')};https://0.0.0.0:{httpsSetup.Port}";
}

builder.WebHost.UseUrls(urls);

// 服务器证书**延迟到握手那一刻才取**（`ServerCertificateSelector` 而不是 `ServerCertificate`）：
//   · 证书是本地生成的，第一次启动要现签一次（几十毫秒），不该挡住"服务起来"这件事；
//   · 生成失败时它会返回 null，Kestrel 在握手时明确报"没有服务器证书" ——
//     **宁可 https 明确不可用，也不能悄悄降级成明文**（那正是这个功能要避免的事）。
//   · 用 `(context, options)` 这个重载是为了拿到 RequestServices：回调发生在 DI 容器建好之后。
if (httpsSetup.Enabled)
{
    builder.WebHost.ConfigureKestrel((context, options) =>
    {
        options.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificateSelector = (_, _) =>
                options.ApplicationServices.GetRequiredService<HttpsCertificateProvider>().Certificate;
        });
    });
}

// ⚠️ 端口必须从**最终的 urls** 里取，不能再用 Server:Http:Port。
//    M2-E5 之后 Server:Http:Urls 可能来自数据目录里的 server-overrides.json，
//    两者不一致时若还读 Server:Http:Port，启动后自动打开的就是一个错端口。
var listeningPort = ServerAddressService.TryExtractPort(
    urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    out var parsedPort)
    ? parsedPort
    : builder.Configuration.GetValue("Server:Http:Port", 8899);

// ═══════════════════════════════════════════════════════════════
// 4.5 单实例：已经在跑就不再起第二个
//
//      为什么必须有：WinExe **没有控制台**，第二个实例若只是"端口占用 + 写 Console.Error"，
//      用户看到的就是"双击了、闪一下、什么都没发生"——最糟糕的失败形态。
//
//      已拍板行为：检测到已有实例 → 直接把浏览器指向它 → 退出码 0（用户目的达成）。
//
//      ⚠️ 命名互斥体默认只在**本会话内**可见：Windows 服务位于 Session 0，与用户会话里的
//         双击互不相见 —— 这正是想要的（"服务在跑时用户又双击"由下面端口占用那条路径
//         兜底，会弹 MessageBox 说明原因）。
// ═══════════════════════════════════════════════════════════════
const string SingleInstanceMutexName = @"Local\ControlServer.Server.SingleInstance";

IDisposable? singleInstanceLock = null;

// ⚠️ 必须可关：测试宿主会在**同一个进程里**反复新建/重启宿主
//    （ConfigStoreTests / TimerFireStoreTests 的 "survives_host_restart"），
//    而互斥体是**进程级**的 —— 不关掉的话第二个宿主会被判定为"已有实例"，
//    Program 提前 return 而不建 IHost，测试报
//    "The entry point exited without ever building an IHost"。
var singleInstanceEnabled = builder.Configuration.GetValue("Server:Startup:SingleInstance", true);

if (singleInstanceEnabled && OperatingSystem.IsWindows() && !WindowsServiceHelpers.IsWindowsService())
{
    singleInstanceLock = WindowsInteractiveHost.TryAcquireSingleInstance(SingleInstanceMutexName);

    if (singleInstanceLock is null)
    {
        // 尊重配置开关：AutoOpenBrowser=false 时（脚本/服务化场景）不要弹浏览器。
        var autoOpenOnSecondLaunch = builder.Configuration.GetValue("Server:Startup:AutoOpenBrowser", true);

        Log.Information(
            "检测到本机已有实例在运行，打开管理界面 http://localhost:{Port}/ 后退出（单实例；AutoOpenBrowser={Auto}）。",
            listeningPort,
            autoOpenOnSecondLaunch);

        using (var earlyLoggerFactory = LoggerFactory.Create(logging => logging.AddSerilog(Log.Logger)))
        {
            BrowserLauncher.TryOpen(
                listeningPort,
                enabled: autoOpenOnSecondLaunch,
                isServiceMode: false,
                earlyLoggerFactory.CreateLogger("Startup"));
        }

        await Log.CloseAndFlushAsync();
        return;
    }
}

// ═══════════════════════════════════════════════════════════════
// 5. 强类型配置（启动即校验）
// ═══════════════════════════════════════════════════════════════
builder.Services
    .AddOptions<ServerOptions>()
    .Bind(builder.Configuration.GetSection(ServerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ═══════════════════════════════════════════════════════════════
// 6. 内核与基础设施（DI）
// ═══════════════════════════════════════════════════════════════
builder.Services.AddSingleton<IDataDirectory>(dataDirectory);

// ── 业务日志（M6.5：保留策略 + 写失败可见）──
//
// 🔥 这里不能用裸的 AddSingleton<LogService>()：那样拿到的是默认策略
//    （30 天 / 200 MB），运维在 appsettings.json 里改了 Server:Logging 也不会生效 ——
//    "配置项存在但没人消费"是本仓已经踩过的坑（AllowedCidrs 曾全仓 0 处消费）。
builder.Services.AddSingleton(sp =>
{
    var logging = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Logging;

    return new LogService(
        sp.GetRequiredService<IDataDirectory>(),
        retention: logging.ToRetention());
});

// ── HTTPS（M5.3）──
//
// 🔥 这个端口号有**两个**消费者，必须同源（见 HttpsSetup 的说明）：
//    · Program.cs 拿它去拼监听地址；
//    · ServerAddressRegistry 拿它去判"这条监听地址该显示成 http 还是 https"。
//    两处各推一遍的话，某次改配置就会只改对一边，而症状是
//    "状态页写着 http://…:8443，运维照着重启一次仍然连不上"。
builder.Services.AddSingleton(httpsSetup);
builder.Services.AddSingleton<HttpsCertificateProvider>();

// ── 来源网段白名单（M6.2 接线）──
//
// 🔥 审计发现：Server:Security:AllowedCidrs 从 M1 起就写在 SecurityOptions 里，
//    注释写着"留空 = 不限制"，但**全仓 0 处消费** —— 运维填了也不生效。
//    一个只存在于配置文件里的防火墙，比没有防火墙更危险（人会因此不再做别的防护）。
//
// 解析失败 ⇒ 直接拒绝启动（fail-closed）：写错一个字符就静默放行，
// 等于把这层保护变成一句许愿。宁可起不来，也不要"看起来起了"。
NetworkAllowList networkAllowList;
try
{
    networkAllowList = NetworkAllowList.FromConfiguration(
        builder.Configuration.GetSection($"{ServerOptions.SectionName}:Security:AllowedCidrs"));
}
catch (FormatException ex)
{
    Log.Fatal(ex, "来源网段白名单（Server:Security:AllowedCidrs）配置有误，拒绝启动：{Message}", ex.Message);
    await Log.CloseAndFlushAsync();

    // 🔥 退出码必须是**非零**。
    //    只 return 不够：Windows 服务控制管理器会把"退出码 0"当成正常停止，
    //    安装脚本也会判成"跑过了"，于是现场看到的是"服务起来了又没了"，
    //    而真正的原因（配置写错）只躺在日志里没人翻。
    //    非零退出码会让 sc 的恢复策略与安装脚本都把它当失败 —— 这正是我们要的。
    Environment.ExitCode = 1;
    return;
}

builder.Services.AddSingleton(networkAllowList);

// 口令哈希（M6.2）：PBKDF2-HMAC-SHA256，同时兼容 M1 的单轮 SHA-256 旧值，
// 并在下次成功登录时就地升级（见 AuthController.Login 的 NeedsRehash 分支）。
//
// 🔥 不要改回 Sha256PasswordHasher：单轮 SHA-256 在现代 GPU 上等于明文，
//    而本系统的 control.db 会随备份离开服务器（M6.1 的产物就是这样）。
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<NetworkService>();

// ── 时钟校准（M2-D）：SNTP 取偏移 → 应用内校正（不动系统时钟）──
// SntpClient 是纯 UDP、无平台依赖，因此实现放 Core；这里只负责「配置 → 参数」的注入。
builder.Services.AddSingleton<ISntpClient, SntpClient>();
builder.Services.AddSingleton(sp =>
{
    var time = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Time;

    // ⚠️ 配置绑定对 List<string> 是**追加**语义：若 TimeOptions.NtpServers 自带初始值，
    //    再配上 appsettings.json 的同名列表就会翻倍（实测 3 → 6），且用户无法删掉内置默认。
    //    因此 TimeOptions.NtpServers 的初始值刻意留空，内置默认只在这里补。
    var servers = time.NtpServers.Count > 0 ? time.NtpServers : TimeSyncSettings.DefaultNtpServers;

    return new TimeSyncSettings(
        servers,
        TimeSpan.FromMilliseconds(time.NtpTimeoutMs),
        TimeSpan.FromSeconds(time.DriftWarnThresholdSeconds));
});
builder.Services.AddSingleton(sp =>
{
    var time = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Time;
    return new TimeSyncHostedOptions(time.AutoSync, TimeSpan.FromSeconds(time.ResyncIntervalSeconds));
});
builder.Services.AddSingleton<TimeSyncService>();
builder.Services.AddHostedService<TimeSyncHostedService>();
builder.Services.AddSingleton<PairingListenerService>();
builder.Services.AddSingleton<ServerState>();

// ── 监听地址热切换（M2-E5）──
// 前置阅读：ServerAddressService 的类注释（为什么不重启 Kestrel）
//           + SwitchableConnectionListener 的类注释（怎么做到只换 socket）
builder.Services.AddSingleton<ServerAddressRegistry>();
builder.Services.AddSingleton(sp => new ServerAddressOverrideStore(sp.GetRequiredService<IDataDirectory>()));
builder.Services.AddSingleton<IPortProbe, TcpPortProbe>();

// 把 Kestrel 的 socket 监听器包成"可换端口的"。
//
// ⚠️⚠️ 这里用的是【追加】（AddSingleton），不是【插队】（Insert(0)）—— 反直觉，但必须如此：
//   Kestrel 会把 DI 里的工厂列表【反转】后装进 TransportManager。实测：
//     DI 顺序         = [Switchable, SocketTransportFactory, NamedPipeTransportFactory]
//     TransportManager= [NamedPipeTransportFactory, SocketTransportFactory, Switchable]
//   于是"插到最前"反而让自己排到最后，SocketTransportFactory 一路抢先，
//   本工厂连 CanBind 都不会被调用（症状是静默的：不报错、日志里连"已接管"都没有，
//   只有端口切换时才表现为"切了但没反应"）。
//   追加注册 → 反转后正好排到第一位 ✔️
//   启动自检（ServerAddressRegistry.HasListener）会兜住这个假设：一旦将来 .NET 改了顺序行为，
//   日志里会立刻出现"未能接管监听地址"，而不是等到用户点改端口才发现。
//
// 内层工厂走"延迟解析"：如果在构造本工厂时就去解析
// IEnumerable<IConnectionListenerFactory>，会连带构造我自己 ⇒ DI 递归。
// 推迟到第一次 BindAsync（那时本单例已进缓存）再解析，并挑出"不是我的那一个"就安全了。
builder.Services.AddSingleton<IConnectionListenerFactory>(sp =>
    new SwitchableConnectionListenerFactory(
        () => sp.GetServices<IConnectionListenerFactory>()
            .First(f => f is not SwitchableConnectionListenerFactory),
        sp.GetRequiredService<ServerAddressRegistry>(),
        sp.GetRequiredService<ILogger<SwitchableConnectionListenerFactory>>()));

builder.Services.AddSingleton<ServerAddressService>();
builder.Services.AddSingleton<StatusProvider>();

// ── 收发日志（M2-B 最小实现：内存环形 + 事件；M2-E 补历史落盘与查询 API）──
// 同时注册具体类型与端口接口：需要统计 / 清空的宿主用具体类型，
// 业务代码（ActionExecutionService 等）只依赖 ITrafficLog。
builder.Services.AddSingleton<TrafficLogService>();
builder.Services.AddSingleton<ITrafficLog>(sp => sp.GetRequiredService<TrafficLogService>());

// ── 指令库（M2-B：读 + 解析 + 整体保存；M2-D 补 CRUD 与 API）──
builder.Services.AddSingleton<CommandLibraryService>();
builder.Services.AddSingleton<ICommandLibrary>(sp => sp.GetRequiredService<CommandLibraryService>());

// ── 动作执行（M2-B）：多动作顺序 + 延迟 + 编码 + 指令库解析 + 收发日志 ──
builder.Services.AddSingleton<ActionExecutionService>();

// ── 对接码派发（M2-C）：三级匹配（按钮 → 子按钮 → 动作）+ 命中即执行 ──
// PairingMatchService 是纯静态函数，无需注册；派发服务依赖配置 / 执行 / 收发日志。
builder.Services.AddSingleton<PairingDispatchService>();

// ── 配置持久化（M2）：JSON 文档 + 乐观锁 ──
// ConfigDocumentStore 必须是 Singleton（乐观锁的串行化要进程级唯一），
// 它内部用 IServiceScopeFactory 开 scope 拿 Scoped 的 DbContext。
builder.Services.AddSingleton<IConfigDocumentStore, EfConfigDocumentStore>();
builder.Services.AddSingleton<ControlSettingsService>();

// ── 定时触发记录（M2-D）：复合主键即幂等键，由数据库保证"一个触发点只跑一次" ──
builder.Services.AddSingleton<ITimerFireStore, EfTimerFireStore>();

// ── 定时调度（M2-D）──
// TimeProvider 统一用「系统钟 + NTP 校正」的实现：调度器只依赖 TimeProvider（便于测试注入假时钟），
// 至于校正量从哪来，由这个实现决定，Core 里不必再散着读 BeijingTime.ClockOffset。
builder.Services.AddSingleton<TimeProvider>(CorrectedTimeProvider.Instance);
builder.Services.AddSingleton(sp =>
{
    var timer = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Timer;
    return new TimerSchedulerOptions(
        Enabled: timer.Enabled,
        TickInterval: TimeSpan.FromSeconds(timer.TickIntervalSeconds),
        CatchUpWindow: TimeSpan.FromSeconds(timer.CatchUpWindowSeconds),
        MissedScanWindow: TimeSpan.FromSeconds(timer.MissedScanWindowSeconds),
        RetentionDays: timer.RetentionDays,
        BackfillMissed: timer.BackfillMissed,
        MaxBackfillCount: timer.MaxBackfillCount);
});
builder.Services.AddSingleton<TimerSchedulerService>();
builder.Services.AddHostedService<TimerSchedulerHostedService>();

// ── 配置备份（M2-F）：导出内嵌图片的单文件 / 导入兼容旧版 .controlbak ──
builder.Services.AddSingleton<ConfigBackupService>();

// ── 图片存储（M2-G1）：上传落盘 / 删除 / 文件名安全解析 ──
// 上传上限经 ImageStoreOptions 注入（Core 不依赖配置框架），
// 同一个值还要喂给下面的 FormOptions —— 那里才是真正的"硬闸"。
builder.Services.AddSingleton(sp => new ImageStoreOptions(
    sp.GetRequiredService<IOptions<ServerOptions>>().Value.Images.MaxUploadBytes));
builder.Services.AddSingleton<ImageStorageService>();

// 上传请求体的硬上限。
//
// FormOptions 的默认值是 128 MB —— 对"4 MB 的图片上传"来说过于宽松：那意味着一个恶意请求
// 能逼服务端先缓冲 128 MB 才轮到端点去拒绝它。
// 设为「单图上限 + 1 MB」（余量给 multipart 的边界与表单头，实际开销不到 1 KB），
// 框架会在**读取过程中**中止超限请求，内存占用被压在配置值附近。
builder.Services.AddOptions<FormOptions>()
    .Configure<IOptions<ServerOptions>>((form, server) =>
    {
        form.MultipartBodyLengthLimit = server.Value.Images.MaxUploadBytes + (1024 * 1024);
    });

builder.Services.AddScoped<DatabaseInitializer>();

// ── 用户账号（M3 RBAC）：Scoped —— 它直接操作 Scoped 的 AppDbContext，且要参与请求级事务 ──
builder.Services.AddScoped<UserAccountService>();

// ── 设备授权（M3 授权子系统）──
//
// 与 RBAC 的分工：RBAC 判「人」，授权判「设备」。两者闸门独立、白名单不同。
//
// 验签器持有一把 ECDsa 实例（非线程安全，内部已串行化），公钥**编译期内嵌**
// （不做成配置项 —— 那等于让任何能改配置的人自签一份万能授权，安全性当场归零，
//  详见 VendorPublicKey 的类注释）。
//
// LicenseService 是 Singleton：授权状态是设备级的、要对所有在线会话同时生效，
// 而闸门中间件在**每个请求**上都要读它 —— 那里绝不能做文件 IO。
builder.Services.AddSingleton<ILicenseStore, LicenseFileStore>();
builder.Services.AddSingleton<ILicenseSignatureVerifier>(_ => EcdsaLicenseVerifier.FromVendorPublicKey());

// 可信时间：只做"翻译"，复用既有的 SNTP + 应用内校正，不新增第二条授时通道。
builder.Services.AddSingleton<ILicenseTimeSource, LicenseTimeSource>();
builder.Services.AddSingleton<LicenseService>();

// 授权闸门的运行时状态：把"该不该拦"收敛成一个布尔量，供 HTTP 中间件与两个后台服务共用。
// 它订阅 LicenseService.Refreshed，因此**必须先于** LicenseHostedService 被构造 ——
// 宿主按注册顺序构造 IHostedService，而 LicenseHostedService 是最后一个，
// 届时本单例已由 PairingListenerHostedService 解析出来，订阅就位。
builder.Services.AddSingleton<LicenseGateState>();

// ═══════════════════════════════════════════════════════════════
// 6.5 客户端 Agent（M4.1）
//
//     三层：客户端 Agent（客户机上）→ 中心服务端（本进程）→ 浏览器。
//     本进程负责：应答发现广播、登记注册、维持长连接、判定在线状态、托管安装包。
//
//     ⚠️ 三条边界（违反会让"授权体系"或"部署边界"漏掉）：
//       ① 客户端凭据只存哈希（库被拷走也拿不到身份）；
//       ② 在线状态只由心跳时间判定（不做 ping / TCP 探测，避免假告警）；
//       ③ 服务端**不主动扫描子网** —— 方向永远是"客户端连出来"，
//          这样客户端一个入站端口都不用开（Windows 防火墙 / NAT / IP 变化一并解决）。
// ═══════════════════════════════════════════════════════════════

// 运行期事实：端口可能来自数据目录里的覆盖文件（M2-E5），所以必须用**真正在听的**那个值。
builder.Services.AddSingleton(sp =>
{
    var agentOptions = sp.GetRequiredService<IOptions<ServerOptions>>().Value;

    return new ServerRuntimeInfo(
        listeningPort,
        agentOptions.Listener.PairingPort,
        agentOptions.Discovery.Port,
        ServerRuntimeInfo.ResolveVersion());
});

builder.Services.AddSingleton<IAgentHostStore, EfAgentHostStore>();
builder.Services.AddSingleton<AgentHostRegistry>();
builder.Services.AddSingleton<AgentEnrollmentService>();
builder.Services.AddSingleton<AgentInstallerLocator>();

// 客户机配置（M4.5）：存储 + 读写/下发服务。
// AgentConfigService 是 Singleton 且内部持有"版本号内存缓存" —— 心跳应答（100 台时 10 次/秒）
// 要回传当前版本，走缓存可以完全不碰库。它是**唯一**的写者，"两处数字不一致"因此不成立。
builder.Services.AddSingleton<IAgentConfigStore, EfAgentConfigStore>();
builder.Services.AddSingleton<AgentConfigService>();

// UDP 指令代发（M4.6）：持有由客户机上报的"通道密钥"（内存，随重连重报）。
// Singleton 是必须的 —— 密钥表本身就是"服务端进程级"的事实，每次解析都新建会立刻丢光。
// 它只被 AgentHub（收上报）与 AgentsController（代发）使用，不放任何后台任务里。
builder.Services.AddSingleton<AgentUdpCommandService>();

// 遥测（M4.2）：存储 + 摄入/查询服务。
// 两者都是 Singleton：摄入是"内存缓冲 + 热层"的有状态对象，
// 而 Singleton 正好让"同一台机器的批次按到达顺序进同一个缓冲"这件事变得简单。
builder.Services.AddSingleton<IAgentTelemetryStore, EfAgentTelemetryStore>();
builder.Services.AddSingleton<AgentTelemetryService>();

// 客户机编码能力（M5.0 第二批）：**只有内存态**，刻意不落库。
// 理由见 AgentEncoderCapabilityService 的类注释：这份数据每次连接都会重报，
// 而"中心重启 ⇒ 连接全断 ⇒ 全部重报"这条链让持久化变成纯粹的负担。
builder.Services.AddSingleton<AgentEncoderCapabilityService>();

// 远程桌面会话（M5.1）：账本 + 编排 + 收尸人 + 输入中继（M5.2）。
//
// 四者都 Singleton，且理由各不相同：
//   · RemoteSessionRegistry —— 它是"谁在推、推到哪儿了、谁在看"的**唯一**账本，
//     每路会话挂着自己的转发队列与后台任务，多实例会立刻变成"帧发给了另一个实例的分组"；
//   · RemoteSessionService —— 无状态，只是一层"校验 + 下发指令"（Singleton 省下每请求一次构造）；
//   · RemoteInputRelay（M5.2）—— 它按会话持有"还没发出去的输入缓冲"（合并连续移动用），
//     多实例会让合并这件事失效（两条移动各进一个实例，谁都没合掉谁）；
//   · Sweeper（HostedService）—— 它就是个每 5 秒醒一次的循环。
//
// ⚠️ 账本**刻意不落库**：会话的生命周期不可能长过长连接，
//    落库只会留下"重启前就死了、数据库里还写着 streaming"的僵尸行。
//    理由与 AgentEncoderCapabilityService 完全同构（见那边的注释）。
//
// ⚠️ 注册顺序在这里**有实际含义**：账本的构造函数要拿中继（会话结束时叫它忘掉缓冲），
//    所以中继必须写在账本前面。写在后面也能跑（容器会按需解析），
//    但那种"隐式顺序"在读到这段代码的人眼里就是随机的。
builder.Services.AddSingleton<RemoteInputRelay>();
builder.Services.AddSingleton<RemoteSessionRegistry>();
builder.Services.AddSingleton<RemoteSessionService>();

// 屏清单缓存（M5.5）：与编码能力同一套规矩 —— 只放内存、每次上报覆盖、
// 机器被移除时顺手清掉（见 AgentScreenDirectory 的类注释）。
builder.Services.AddSingleton<AgentScreenDirectory>();
builder.Services.AddHostedService<RemoteSessionSweeperHostedService>();

// 客户机目录浏览（M4.4+，回传源路径下拉用）。
// 无状态：它做的事就是"按 agentId 找到那条长连接、InvokeAsync 问一句、掐超时"。
builder.Services.AddSingleton<AgentFileBrowseService>();

// 客户机「接收到的数据」（2026-10-13，排查"指令不生效"用）。
// 与目录浏览同一个形状、同一条理由：只是一次"问过去、等回话、掐超时"，
// 没有任何自己的状态。
builder.Services.AddSingleton<AgentReceivedDataService>();

// 网络唤醒（WoL，2026-10-15，需求③）：读该机的硬件清单拿网卡 MAC，
// 再用 NetworkService 发魔术包。无状态 ⇒ Singleton。
builder.Services.AddSingleton<AgentWakeService>();

// 文件仓库与分发（M4.4）：存储 + 仓库服务 + 分发服务。
// 三者都 Singleton：仓库服务持有"暂存目录"这类无状态事实；分发服务要往外推事件，
// 而推事件本身是无状态的。文件字节的读写走 System.IO，不经 EF。
builder.Services.AddSingleton<IFileRepositoryStore, EfFileRepositoryStore>();
builder.Services.AddSingleton<IDistributionStore, EfDistributionStore>();
builder.Services.AddSingleton(sp =>
{
    var files = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Files;
    return new FileRepositoryOptions
    {
        MaxFileBytes = files.MaxFileBytes,
        ChunkSizeBytes = files.ChunkSizeBytes,
        SessionExpiryHours = files.SessionExpiryHours,
        OrphanGraceHours = files.OrphanGraceHours,
    };
});
builder.Services.AddSingleton<FileRepositoryService>();
// 对账（M7.1）：库 ↔ 盘 的孤儿/断链。与 FileRepositoryService 分开是刻意的 ——
// 前者只管"文件还在不在、该不该回收"，后者管业务语义，混在一起会让删除路径多出分支。
builder.Services.AddSingleton<RepositoryGroomer>();
builder.Services.AddSingleton<DistributionService>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dataDirectory.DatabaseFile}"));

// ── 平台实现按 OS 注册（组合根唯一的分支点） ──
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IMachineCodeProvider, WindowsMachineCodeProvider>();

    // 托盘图标：绿色版（双击、无控制台窗口）用它来"退出服务"。
    // 服务形态（Session 0）里 IsAvailable=false —— WindowsTrayIconHost 会自己判，
    // 调用处不需要再分支。
    builder.Services.AddSingleton<ITrayIconHost, WindowsTrayIconHost>();

    // ⚠️ M5.0 第二批起，**这里刻意不再注册 IEncoderCapabilityProbe**（第一批曾注册过）。
    //
    // 原因：要编码的是**被控端**。中心这台机器上有没有 NVENC，与"某台客户机能不能跑远控"
    // 毫无关系 —— 在中心本地探测只能回答一个错误的问题，而一个"注入了却没人该用"的服务
    // 是下一轮改动最容易踩空的坑（看着有实现，其实问题在别处）。
    // 探测实现在 Platform.Windows / Platform.Linux 里照旧可用（客户机与用例都在用），
    // 只是它的宿主换成了 **客户机**：探测 → 经 SignalR 上报 → 中心只负责保存与展示。
}
else
{
    builder.Services.AddSingleton<IMachineCodeProvider, LinuxMachineCodeProvider>();

    // Linux 无桌面托盘：注册 Noop，让上层代码完全不必出现 if (Linux)。
    builder.Services.AddSingleton<ITrayIconHost, NoopTrayIconHost>();
}

// ── 实时通道 ──
//
// ⚠️ 单条消息上限从默认的 32 KB 提上来，历史上调过一次 512 KB：
//    **目录浏览的应答**曾经是这里的最大值（最多 300 个条目，而目录名/路径可以长到 255 字符，
//    最坏情况能到 80 KB 上下），但 M5.1 起真正的最大值变成了**远控的一帧画面**（见下）。
//    超出上限的后果不是"截断"而是**整条消息被丢弃** —— 表现为"浏览某个目录永远失败，
//    换个目录又好了"（或"某一帧之后画面再也不动"），那种只在特定输入下复现的故障最难查。
//    其余消息（心跳、指标批次、配置变更通知）都远小于默认值，提上限不改变它们的行为。
//
// 🔥🔴 **M5.4-b 抬到 8 MB，而且这个数只在 `RemoteStreamLimits` 里定义一次。**
//
//    M5.1/M5.2 那一版是 512 KB，理由写的是"MJPEG 到 1080p 级够用"。M5.4-a 实测之后
//    这个前提没了：2160×1440 的 **H.264 IDR 单帧 306.9 KB**，而 512 KB 经 base64（+33%）
//    只留给裸码流约 384 KB ⇒ **余量只剩 20%**；4K 的像素是它的 2.67 倍，
//    IDR 量级 800 KB 起，**必然撞穿**。
//
//    ⚠️ 撞穿的代价被严重低估过：**这里超出上限不是"丢掉这一条消息"，而是掐断整条连接**。
//    而这条连接上跑的不只是视频 —— 心跳、遥测、配置下发、远控音频全在它上面。
//    于是"某一帧大了几十 KB"在现场表现为**整台客户机掉线**（心跳一起没了、在线表变灰），
//    排查会一路查向网络，离真正的原因十万八千里。
//
//    🔸 所以被控端也在写管道前自己判一次（`RemoteStreamProtocol.WriteFrameAsync`），
//    而它用的**就是下面这个常量**（见 `RemoteStreamLimits.SignalRMessageBytes`）——
//    "客户机能发多大"与"中心愿意收多大"必须是同一个数，各写一份必然漂，
//    而漂的方向只有一种：客户机还在发、中心已经掐了。
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = RemoteStreamLimits.SignalRMessageBytes);
builder.Services.AddSingleton<INotifier, SignalRNotifier<EventsHub>>();

// ── 认证 / 授权 ──
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {        options.Cookie.Name = "cs.sid";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;

        // API 语义：未登录返回 401，而不是 302 跳转登录页。
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // ── 逐请求复核会话（M3 RBAC 的关键一环）──
        //
        // Cookie 里带的是一份**签发时的快照**。没有这一步，"把某人降级成 Viewer"
        // 或"停用某账号"在对方 Cookie 过期（最长 12 小时）之前**完全不生效** ——
        // 权限分级会变成"最长 12 小时后才生效"的承诺，而不是保证。
        //
        // 这里每个请求做一次主键查询：账号不存在 / 已停用 → 直接作废会话；
        // 角色或改密标记变了 → 用最新状态换掉主体（ShouldRenew 会重签 Cookie）。
        //
        // ⚠️ 已知取舍：**基础设施异常时选择放行（fail-open）**。SQLite 在并发下偶发 BUSY，
        //    若因此把所有人踢下线，代价远大于"DB 故障期间多放行几个请求"——
        //    何况 DB 真坏时，后续业务请求自己也会失败。异常一定会被记进日志。
        options.Events.OnValidatePrincipal = async context =>
        {
            var userId = SessionPrincipal.ReadUserId(context.Principal);

            // M3 之前签发的 Cookie 没有账号 Id，无法复核 —— 直接作废，让用户重新登录。
            if (userId is null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                var accounts = context.HttpContext.RequestServices.GetRequiredService<UserAccountService>();
                var user = await accounts
                    .FindAsync(userId.Value, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                if (user is null || !user.IsEnabled)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                        .ConfigureAwait(false);

                    Log.Information(
                        "会话已作废：账号 Id={UserId} {Reason}",
                        userId.Value,
                        user is null ? "已不存在" : "已被停用");
                    return;
                }

                var stale =
                    !string.Equals(SessionPrincipal.ReadUserName(context.Principal), user.UserName, StringComparison.Ordinal)
                    || !string.Equals(SessionPrincipal.ReadRole(context.Principal), user.Role, StringComparison.Ordinal)
                    || SessionPrincipal.ReadMustChangePassword(context.Principal) != user.MustChangePassword;

                if (stale)
                {
                    context.ReplacePrincipal(SessionPrincipal.Create(user));
                    context.ShouldRenew = true;

                    Log.Information("会话声明已刷新：{User}（{Role}）", user.UserName, user.Role);
                }
            }
            catch (Exception ex)
            {
                // 见上方"已知取舍"：放行并记日志。
                Log.Warning(ex, "会话复核失败（按已认证处理）：账号 Id={UserId}", userId.Value);
            }
        };
    })
    // ── 客户端机器的认证 scheme（M4.1）──
    // 挂在 AddCookie **之后**：默认 scheme 仍是 Cookie，这个只被显式点名时启用。
    // ⚠️ 同一 scheme 名只能注册一次 —— 重复 AddScheme 会直接抛 InvalidOperationException。
    .AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentAuthentication.Scheme, null);

// ── 客户端 Agent 的认证 scheme（M4.1）──
//
// 与 Cookie **并列而不替代**：Cookie 认"人"，这个 scheme 认"机器"。
// 不加它，Agent 就没法连 /hubs/agent；而把机器身份塞进 Cookie 体系，
// 会让"停用某个账号"和"吊销某台机器"在日志与代码里纠缠不清。
//
// 它只在显式标注 [Authorize(AuthenticationSchemes = AgentKey)] 的地方生效
// （控制器挂在 AddCookie 链的**末尾**注册，因此默认 scheme 仍是 Cookie）。
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AdminOnly, policy => policy.RequireRole(Roles.Admin));
    options.AddPolicy(Policies.CanOperate, policy => policy.RequireRole(Roles.Admin, Roles.Operator));

    // ── 文件仓库（M4.4）──
    //
    // 这两条策略**同时接受"人"（Cookie）与"机器"（AgentKey）**，但仍然挡得住不该放的行：
    //   · FileRead ：Admin / Operator，或一台合法客户机（下发时字节由客户机用 Range 拉走）。
    //                刻意**不含 Viewer** —— 仓库里可能有客户机回传的现场日志。
    //   · FileIngest：Admin，或一台合法客户机（回传上传）。**不含 Operator** ——
    //                能往仓库写文件就等于能把任意内容推给所有客户机。
    //
    // 判据用"有没有 AgentId 声明"来识别机器身份，而不是看 Identity.AuthenticationType：
    // 后者会随 scheme 名的改动而失效，而这条声明是 AgentKey handler 亲手写进去的。
    const string cookieScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
    var agentSchemes = new[] { cookieScheme, AgentAuthentication.Scheme };

    options.AddPolicy(Policies.FileRead, policy => policy
        .AddAuthenticationSchemes(agentSchemes)
        .RequireAssertion(context =>
            context.User.FindFirst(AgentAuthentication.ClaimAgentId) is not null
            || context.User.IsInRole(Roles.Admin)
            || context.User.IsInRole(Roles.Operator)));

    options.AddPolicy(Policies.FileIngest, policy => policy
        .AddAuthenticationSchemes(agentSchemes)
        .RequireAssertion(context =>
            context.User.FindFirst(AgentAuthentication.ClaimAgentId) is not null
            || context.User.IsInRole(Roles.Admin)));

    // AgentConfigRead（M4.5）：Admin / Operator，或一台合法客户机。
    //   与 FileRead 同构 —— 同一份数据，"人"读它是为了展示编辑，"机器"读它是为了执行。
    //   ⚠️ **归属校验在控制器里**（客户机只能读自己那一份），策略判断不了"是不是这台"。
    options.AddPolicy(Policies.AgentConfigRead, policy => policy
        .AddAuthenticationSchemes(agentSchemes)
        .RequireAssertion(context =>
            context.User.FindFirst(AgentAuthentication.ClaimAgentId) is not null
            || context.User.IsInRole(Roles.Admin)
            || context.User.IsInRole(Roles.Operator)));

    // AgentSelfReport（2026-10-15，需求①）：客户机报告"我要换中心了"。
    //   与 AgentConfigRead 同构但方向相反；这里**只认机器身份**（外加 Admin，便于用脚本复核）——
    //   Operator 不该替机器宣告"它要搬走"。
    options.AddPolicy(Policies.AgentSelfReport, policy => policy
        .AddAuthenticationSchemes(agentSchemes)
        .RequireAssertion(context =>
            context.User.FindFirst(AgentAuthentication.ClaimAgentId) is not null
            || context.User.IsInRole(Roles.Admin)));
});

// ── Web ──
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── 登录限速（M6.2）──
//
// 审计里唯一的"高危候选"落在两件事上，而它们是同一副解药：
//   ① 换着用户名扫一遍（每个名字试 4 次，永不触发 5 次锁定）—— 账号锁定挡不住；
//   ② 把管理员账号**故意锁死**（输 5 次错密码锁 15 分钟，循环即永久拒绝服务）。
// 按来源 IP 记账，单点只能试有限次，上述两种打法的成本立刻不可接受。
//
// 为什么按 IP 而不是按账号：按账号和既有锁定重复，而且等于送攻击者一件
// "用别人的账号名触发限速"的新武器。
//
// ⚠️ 分区键用 RemoteIpAddress 的**字符串**：TestServer 下它可能是 null
//    （全部落进 "unknown" 分区），所以测试宿主把 PermitLimit 调到极大，
//    见 ControlServerFactory 里 Server__Auth__LoginRateLimit__PermitLimit 那一行。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(AuthRateLimitPolicies.Login, context =>
    {
        var limit = context.RequestServices
            .GetRequiredService<IOptions<ServerOptions>>().Value.Auth.LoginRateLimit;

        // 关闭限速不是"不挂策略"，而是把额度抬到实际上无限 ——
        // 策略本身仍挂在端点上，"策略名对不上导致静默失效"这种事故就不会发生。
        var permitLimit = limit.Enabled ? limit.PermitLimit : int.MaxValue;
        var window = limit.Enabled ? TimeSpan.FromMinutes(limit.WindowMinutes) : TimeSpan.FromMinutes(1);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });

    options.OnRejected = (context, _) =>
    {
        // 限速命中不写滚动日志：拒绝本身可能是攻击流量，逐条落盘会把"防护"
        // 变成"磁盘写满"的加速器（§2.2 已经有一条静默断日志的坑）。
        // 现场要看的是"登录被拒了多少次"，那属于接审计的活儿，记在 M6.2 的挂账里。

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new
        {
            message = "登录尝试过于频繁，请稍后再试。",
        }));
    };
});

// ── 上传请求体超限的"人话化"（M2-G1）──
//
// MVC 在**调用 action 之前**就会为请求构建值提供器并读取表单，因此
// "请求体超过 FormOptions.MultipartBodyLengthLimit" 这次失败发生在**我的端点代码之前** ——
// 端点里的 try/catch 根本轮不到。实测（TestServer + 真 Kestrel 行为一致）拿到的是框架默认的
// 400 + ProblemDetails，文案是英文的 "Multipart body length limit 5242880 exceeded"：
// 既有内部数字、又不对用户说人话，而且 413 才是"负载过大"的正确状态码。
//
// 这里只针对这**一种**失败改写响应，其余模型状态错误原样交回框架默认实现。
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    var frameworkDefault = options.InvalidModelStateResponseFactory;

    options.InvalidModelStateResponseFactory = context =>
    {
        if (!IsMultipartBodyTooLarge(context.ModelState)) return frameworkDefault(context);

        var limit = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<ServerOptions>>().Value.Images.MaxUploadBytes;

        return new ObjectResult(new { message = $"图片不能超过 {limit / 1024.0 / 1024.0:0.##} MB。" })
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge,
        };
    };
});

// ── 后台常驻任务 ──
builder.Services.AddHostedService<LogRelayHostedService>();
builder.Services.AddHostedService<RealtimeRelayHostedService>();
builder.Services.AddHostedService<PairingListenerHostedService>();
builder.Services.AddHostedService<HeartbeatHostedService>();
builder.Services.AddHostedService<StatusBroadcastHostedService>();

// 授权宿主：首次启动自动生成本机试用 + 周期刷新状态。
// 放在后台任务而不是启动自检里 —— 生成试用要取网络时间（SNTP 最多 3×超时），
// 不该拖慢端口监听；而且首次启动时档位还是 Warn，什么都不会拦。
builder.Services.AddHostedService<LicenseHostedService>();

// ── 客户端 Agent（M4.1 / M4.2）的后台任务 ──
//
// ① 在线状态巡检：每 5 秒算一遍状态、只推"跃迁"、每 60 秒把心跳成批落盘。
// ② 发现应答：UDP 默认 9998，收到 CS-DISCOVER 回 CS-HERE（**服务端不主动扫子网**）。
// ③ 遥测编排：落盘（默认 30 秒一批）/ 实时推送（默认 2 秒节流）/ 保留清理 / 日聚合。
// ④ 文件传输维护：回收过期上传会话的暂存文件 + 清理陈旧的分发任务。
//
// 注册顺序无所谓：四者互不依赖，且都不阻塞启动。
builder.Services.AddHostedService<AgentPresenceHostedService>();
builder.Services.AddHostedService<DiscoveryListenerHostedService>();
builder.Services.AddHostedService<AgentTelemetryHostedService>();
builder.Services.AddHostedService<FileTransferHostedService>();

var app = builder.Build();

// ═══════════════════════════════════════════════════════════════
// 6.5 业务日志的告警出口（M6.5）
// ═══════════════════════════════════════════════════════════════
//
// 🔴 落盘失败时必须走**另一条通道**。接回 LogService 等于没接 ——
//    写不进去的正是那条通道本身。这里接到 Serilog（server-*.log + 控制台），
//    它和业务日志是两个文件、两套滚动策略，一个坏了另一个还在。
{
    var businessLog = app.Services.GetRequiredService<LogService>();
    var loggingOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Logging;

    Log.Information(
        "业务日志保留：{State}（保留 {Days} 天 / 上限 {Mb} MB，文件 logs/log-YYYY-MM-DD.ndjson）",
        loggingOptions.RetentionEnabled ? "启用" : "已关闭（会无限增长）",
        loggingOptions.RetentionDays,
        loggingOptions.RetentionMaxMegabytes);

    businessLog.SinkFailed += (message, exception) => Log.Error(exception, "{Message}", message);
    businessLog.SinkRecovered += () => Log.Warning("业务日志落盘已恢复（之前的失败原因见上一条 Error）。");
}

// ═══════════════════════════════════════════════════════════════
// 7. 启动自检：数据库初始化 / 播种管理员
// ═══════════════════════════════════════════════════════════════
try
{
    using var scope = app.Services.CreateScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    var serverOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

    await initializer.InitializeAsync(
        serverOptions.Auth.DefaultAdminUserName,
        serverOptions.Auth.DefaultAdminPassword);

    Log.Information("数据库就绪：{Db}", dataDirectory.DatabaseFile);

    // 配置自检：让「界面上为什么没有按钮」这个问题的答案出现在启动日志里，
    // 而不是等用户对着空网格发呆。
    var configService = app.Services.GetRequiredService<ControlSettingsService>();
    var configView = await configService.GetAsync();

    var subButtonCount = 0;
    foreach (var set in configView.Settings.SubButtonSets)
    {
        subButtonCount += set.SubButtons?.Count ?? 0;
    }

    Log.Information(
        "配置就绪：状态 {State}，版本 {Version}，按钮 {Buttons} 个，子按钮集 {Sets} 个（子按钮 {Subs} 个）",
        configView.State,
        configView.Version,
        configView.Settings.Buttons.Count,
        configView.Settings.SubButtonSets.Count,
        subButtonCount);

    if (configView.Settings.Buttons.Count == 0 && configView.Settings.SubButtonSets.Count == 0)
    {
        Log.Information("配置为空 —— 可打开网页的「设置」标签页，导入旧版导出的 .controlbak 备份文件。");
    }

    foreach (var note in configView.Notes)
    {
        Log.Information("配置清洗：{Note}", note);
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "【启动失败】数据库初始化失败，服务无法继续");
    await Log.CloseAndFlushAsync();
    Environment.ExitCode = 1;
    return;
}

// ═══════════════════════════════════════════════════════════════
// 8.5 静态资源来源：开发期读磁盘，绿色版读内嵌资源
//     单文件 exe 发布时 wwwroot 已被嵌入程序集（见 .csproj），
//     磁盘上不存在该目录，必须回退到 ManifestEmbeddedFileProvider，
//     否则浏览器访问首页会 404。
// ═══════════════════════════════════════════════════════════════
IFileProvider webRootFileProvider;
var physicalWebRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");

if (Directory.Exists(physicalWebRoot))
{
    webRootFileProvider = new PhysicalFileProvider(physicalWebRoot);
    Log.Information("静态资源来源：磁盘 {Path}", physicalWebRoot);
}
else
{
    webRootFileProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
    Log.Information("静态资源来源：内嵌资源（绿色版 / 单文件模式）");
}

// ═══════════════════════════════════════════════════════════════
// 8.6 时钟口径自检（M2-D）
//     只打印"当前采用的时间口径"；真正的校准由 TimeSyncHostedService
//     在后台异步完成（不阻塞启动，见其类注释）。
// ═══════════════════════════════════════════════════════════════
var timeOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Time;

// 打印**生效**的服务器列表（即 TimeSyncService 真正会去查的那些），
// 而不是配置原文 —— 两者在有内置默认回退时并不相同。
var effectiveNtpServers = app.Services.GetRequiredService<TimeSyncSettings>().NtpServers;

Log.Information(
    "时间口径：{DisplayName}，自动校准={AutoSync}，服务器=[{Servers}]",
    BeijingTime.DisplayName,
    timeOptions.AutoSync ? "启用" : "关闭",
    string.Join(", ", effectiveNtpServers));

if (!timeOptions.AutoSync)
{
    Log.Information("BeijingTime = 系统时钟 + 固定 UTC+8（未启用 NTP 校正，行为与引入校正前完全一致）。");
}
else
{
    Log.Information(
        "【已知局限】Serilog 的滚动文件名切分点使用【系统时钟】"
        + "（Serilog.Sinks.File 的 Clock 是 internal 类型，无法注入校正），"
        + "只有文件内容里的时间戳使用【校正后的北京时间】。"
        + "系统钟偏差跨越零点时可能出现「行内日期与文件名不一致」。");
}

// ═══════════════════════════════════════════════════════════════
// 9. 中间件管线
// ═══════════════════════════════════════════════════════════════
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging(options =>
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0}ms)");

// ── 安全响应头（M6.2）──
//
// 这三条都是"零成本、零兼容风险"的：不需要前端配合，不会改变任何响应体。
//
//   · nosniff          —— 浏览器不再猜内容类型。没有它，一个被上传到仓库的文件
//                        （内容其实是 HTML）被直接访问时，IE/旧版 Chrome 会当页面执行。
//   · X-Frame-Options  —— 禁止被别的站点用 iframe 套住（点击劫持）。
//                        本项目全站无 iframe（审计已 grep 确认），所以 DENY 不会伤到自己。
//   · Referrer-Policy  —— 跨站跳转时不带完整 URL。本系统的 URL 里**不含**令牌
//                        （Agent 的 key 走请求头，不走查询串之外的页面地址），
//                        但把这条钉死，将来就不会因为"某人在 URL 里加了个参数"而漏。
//
// 🔸 CSP 刻意**没有**加：Vue 产物里有内联样式与运行时生成的 style 属性，
//    随便糊一个 `default-src 'self'` 就是白屏，而白屏在展厅现场等于"系统坏了"。
//    要做就必须先在真机上实测一遍（挂账，见 M6 文档 §3.1 的清单）。
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;

    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";

    await next().ConfigureAwait(false);
});

// ── 来源网段白名单（M6.2）──
//
// 只对**配了**白名单的部署生效（空列表 = 不限制，保持既有行为）。
// 位置刻意在 UseStaticFiles 之前：页面本身也属于"谁能访问"的范围，
// 只挡 API 而不挡页面，等于把登录页白送出去。
//
// ⚠️ 被拒的请求**不逐条写日志**：拒绝可能是持续扫描，逐条落盘会把防护变成
//    "磁盘写满"的加速器（§2.2 那条静默断日志的坑）。要看清谁被拒，
//    靠的是请求日志里的 403 行 + 运维侧的网络设备。
if (!networkAllowList.IsEmpty)
{
    Log.Information("来源网段白名单已启用：{Count} 条（回环始终放行）", networkAllowList.Count);

    app.Use(async (context, next) =>
    {
        if (networkAllowList.Allows(context.Connection.RemoteIpAddress))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        await context.Response.WriteAsJsonAsync(new
        {
            message = "来源地址不在允许的网段内（Server:Security:AllowedCidrs）。",
        }).ConfigureAwait(false);
    });
}

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRootFileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webRootFileProvider });

// 登录限速的执行点（策略在 AddRateLimiter 里定义、端点上用 [EnableRateLimiting] 指定）。
// 放在 UseAuthentication 之前：限速不需要知道"你是谁"，先按 IP 拦掉再说，
// 也顺便省下被拒请求的会话复核开销。
app.UseRateLimiter();

app.UseAuthentication();

// ── 强制改密闸门（M3）──
//
// ⚠️ 2026-10-09 起**默认关闭**：产品取消了"首次使用强制改密"——
//    出厂管理员、新建账号、管理员重置密码都不再标记 MustChangePassword，
//    因此默认状态下没有任何账号会被拦。开关保留，供确有需要的部署显式开启。
//
// 为什么保留这个执行点：MustChangePassword 若只写在库和前端弹窗里，它就只是个"建议"——
// 前端弹窗可以被关掉，直接调接口更是完全绕得过去。开启后它是硬的：
// 被标记的账号在改密之前，除白名单外**不能调用任何 /api 接口**。
//
// 为什么只闸 /api：
//   · 静态资源在 UseAuthentication 之前就已返回，压根走不到这里；
//   · /healthz、/readyz 是探活端点，被闸住会让服务被运维判死；
//   · SignalR 若被拦，"改密页面"上的实时日志会断，看起来像服务挂了。
var enforcePasswordChange = builder.Configuration.GetValue("Server:Auth:EnforcePasswordChange", false);

app.Use(async (context, next) =>
{
    if (enforcePasswordChange
        && context.User.Identity?.IsAuthenticated == true
        && SessionPrincipal.ReadMustChangePassword(context.User)
        && context.Request.Path.StartsWithSegments("/api")
        && !IsPasswordChangeAllowlisted(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        await context.Response.WriteAsJsonAsync(new
        {
            message = "该账号必须先修改密码（管理员代设或重置过密码）。",
            mustChangePassword = true,
        }).ConfigureAwait(false);

        return;
    }

    await next().ConfigureAwait(false);
});

app.UseAuthorization();

// ── 授权闸门（M3）──
//
// 与上面的强制改密闸门**相互独立**、语义不同：改密闸门是**账号级**（这个人必须改密），
// 授权闸门是**设备级**（这台机器没被授权）。两者都作用于 /api，但白名单不同 ——
// 授权闸门必须放行"导入授权"（那是现场唯一的自救通道），否则会死锁。
//
// 为什么放在 UseAuthorization **之后**：
//   · 未登录的请求先拿到 401（而不是被误报成"没授权"），语义更准；
//   · 已登录但角色不够的请求先拿到 RBAC 的 403。
//   闸门本身只关心"这台机器能不能执行动作"，排在鉴权之后不会漏掉任何东西
//   —— 因为被拦的那几个端点全都要求 CanOperate，绕过授权闸门也进不去。
//
// 为什么用 GetEndpoint() 判路由而不是拼路径：/api/actions 下 execute（拦）与 preview（放行）
// 语义相反，/api/timers 下也是（GET 总览放行、{refId}/fire 拦），只有路由模板能可靠区分。
// 分类表见 Security/LicenseGate.cs，未归类一律按拦截处理（fail-closed）。
var licenseGate = app.Services.GetRequiredService<LicenseGateState>();

app.Use(async (context, next) =>
{
    if (licenseGate.IsBlocked
        && context.Request.Path.StartsWithSegments("/api")
        && LicenseGate.IsGated(context))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        await context.Response.WriteAsJsonAsync(new
        {
            code = LicenseGate.DeniedCode,
            message = LicenseGate.DeniedMessage,
        }).ConfigureAwait(false);

        return;
    }

    await next().ConfigureAwait(false);
});

app.MapControllers();
app.MapHub<EventsHub>(Routes.EventsHub);

// 客户端 Agent 的长连接（M4.1）：认证走 AgentKey scheme（见 AddScheme 处）。
app.MapHub<AgentHub>(Routes.AgentHub);

// ── 观看端的实时通道（M5.1）──
//
// 方向与上面那条**相反**：那条是"被控机连出来"，这条是"人连进来看画面"。
// 鉴权由 Hub 自己的 [Authorize(AdminOnly)] 承担 —— 决策 11 明确
// "非管理员连只读观看都不允许"，所以这里不加任何匿名放行。
app.MapHub<RemoteHub>(Routes.RemoteHub);

// ── 根证书下载与 https 地址自述（M5.3）──
//
// ⚠️ 刻意**不在** /api 之下（授权闸门只作用于 /api）：一台还没导授权的机器
//    照样要能把根证书装到观看端上，否则就成了"要先有权，才能让浏览器有声音"。
app.MapTrustEndpoints();

// ── 健康检查 ──
//
// ⚠️ 这个端点**永远返回 200**（只要进程活着）。这是刻意的：探活脚本就是靠
//    "能不能连上、返不返 200"来判断进程死活，把业务状态混进状态码会让
//    "日志写不进去"变成"服务挂了"。
// 但业务侧的异常必须**看得见**：`logSink` 就是 M6.5 为"日志悄悄断了"留的观察窗。
app.MapGet("/healthz", (ServerState state, LogService log) => Results.Ok(new
{
    status = "alive",
    serverTime = BeijingTime.Now,
    uptimeSeconds = (long)state.Uptime.TotalSeconds,
    version = ServerRuntimeInfo.DescribeVersion(),
    logSink = new
    {
        healthy = log.ConsecutiveFlushFailures == 0,
        consecutiveFailures = log.ConsecutiveFlushFailures,
        lastError = log.LastFlushError,
    },
}));

app.MapGet("/readyz", async (
    AppDbContext db,
    PairingListenerService listener,
    LicenseGateState licenseGate,
    CancellationToken cancellationToken) =>
{
    var database = await db.Database.CanConnectAsync(cancellationToken);
    var listening = listener.IsRunning;

    // ⚠️ 授权闸门停摆（M3）是**策略决定**，不是故障 —— 重启治不好它。
    //    若把它算作 not-ready，编排器（k8s 探针 / 巡检脚本）会陷入
    //    「重启 → 仍停摆 → 再重启」的死循环，真正的原因（没授权）反而被重启日志淹没。
    //    因此停摆期间仍报 ready，但把真实原因用 licenseBlocked 明确标出来。
    var licenseBlocked = licenseGate.IsBlocked;
    var ready = database && (listening || licenseBlocked);

    var payload = new
    {
        status = ready ? "ready" : "not-ready",
        database,
        listener = listening,
        listenerPort = listener.Port,
        licenseBlocked,
    };

    return ready
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// SPA 回退：交给前端路由。必须显式传入同一个 FileProvider，否则内嵌资源模式下会 404。
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = webRootFileProvider });

// ═══════════════════════════════════════════════════════════════
// 10. 优雅停机
// ═══════════════════════════════════════════════════════════════
app.Lifetime.ApplicationStopping.Register(() => Log.Information("收到停止信号，正在优雅停机…"));

app.Lifetime.ApplicationStopped.Register(async () =>
{
    Log.Information("服务已停止");
    await Log.CloseAndFlushAsync();
});

// 监听真正就绪后再开浏览器（此时 Kestrel 已在监听，页面一定能打开）。
var startupOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
var isServiceHost = WindowsServiceHelpers.IsWindowsService()
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));

app.Lifetime.ApplicationStarted.Register(() =>
    BrowserLauncher.TryOpen(
        listeningPort,
        startupOptions.Startup.AutoOpenBrowser,
        isServiceHost,
        app.Logger));

// ═══════════════════════════════════════════════════════════════
// 10.6 托盘图标 —— 本轮需求的核心
//
//      背景：OutputType=WinExe 之后双击启动**没有控制台窗口**，
//      "关控制台把服务带走"这个问题从根上消失了；相应地，
//      "停止服务"必须有新出口 —— 就是右下角托盘图标的右键菜单。
//
//      服务形态（Session 0）里 IsAvailable=false，这里自然跳过（不写 if (Linux) 也不写 if (服务)）。
// ═══════════════════════════════════════════════════════════════
app.Lifetime.ApplicationStarted.Register(() =>
{
    // 托盘是"锦上添花"，必须能整体关掉：
    //   · 集成测试有 170+ 个宿主，每个都真的建一次托盘图标会刷屏任务栏（见 ControlServerFactory）；
    //   · 有些部署场景（无人值守机、kiosk 主机）也不希望出现托盘。
    if (!builder.Configuration.GetValue("Server:Tray:Enabled", true))
    {
        app.Logger.LogInformation("托盘图标：已禁用（Server:Tray:Enabled=false）。");
        return;
    }

    var tray = app.Services.GetRequiredService<ITrayIconHost>();

    if (!tray.IsAvailable || isServiceHost)
    {
        app.Logger.LogInformation(
            "托盘图标：未启用（{Reason}）",
            isServiceHost ? "当前为服务宿主形态（Session 0 无通知区域）" : tray.UnavailableReason ?? "平台不支持");
        return;
    }

    var dataRoot = dataDirectory.Root;
    var logRoot = dataDirectory.LogDirectory;

    var menu = new List<TrayMenuCommand>
    {
        new("打开管理界面", TrayCommands.OpenConsole, IsDefault: true),
        new("复制访问地址", TrayCommands.CopyAddress),
        TrayMenuCommand.Separator,
        new("打开数据目录", TrayCommands.OpenDataDirectory),
        new("打开日志目录", TrayCommands.OpenLogDirectory),
        TrayMenuCommand.Separator,
        new("退出服务", TrayCommands.Exit),
    };

    tray.CommandInvoked += commandId =>
    {
        switch (commandId)
        {
            case TrayCommands.OpenConsole:
                BrowserLauncher.TryOpen(listeningPort, enabled: true, isServiceMode: false, app.Logger);
                break;

            case TrayCommands.CopyAddress:
                CopyTextToClipboard($"http://localhost:{listeningPort}/", app.Logger);
                break;

            case TrayCommands.OpenDataDirectory:
                OpenInExplorer(dataRoot, app.Logger);
                break;

            case TrayCommands.OpenLogDirectory:
                OpenInExplorer(logRoot, app.Logger);
                break;

            case TrayCommands.Exit:
                // 这条路径有两个来源：托盘菜单「退出服务」，以及外部关闭信号
                // （`taskkill` 不带 /F / 会话注销 → WM_CLOSE，见 WindowsTrayIconHost）。
                app.Logger.LogInformation("收到「退出服务」请求（托盘菜单或外部关闭信号），开始优雅停机。");

                // ⚠️ 必须是 StopApplication 而不是 Environment.Exit：
                //    否则 Serilog 来不及 flush、SQLite 来不及收摊、托盘图标来不及 NIM_DELETE
                //    （图标会残留在通知区域，鼠标划过才消失）。
                app.Lifetime.StopApplication();
                break;
        }
    };

    _ = ShowTrayAsync(
        tray,
        new TrayIconOptions(
            ToolTip: $"中控服务 — http://localhost:{listeningPort}",
            Menu: menu,
            DefaultCommandId: TrayCommands.OpenConsole),
        app.Logger);
});

/// <summary>
/// 显示托盘图标并记录结果。
/// <para>
/// 单独抽成函数是因为 <c>ApplicationStarted.Register</c> 只接受同步委托，
/// 而 <c>ShowAsync</c> 是异步的（要等托盘线程把窗口和图标都建好才返回）。
/// </para>
/// <para>
/// <b>必须记日志</b>：托盘"没出现"是最常见也最难查的故障 —— 有了这一行，
/// 就能一眼区分"平台不支持"、"被服务形态跳过"与"建失败了"。
/// </para>
/// </summary>
static async Task ShowTrayAsync(ITrayIconHost tray, TrayIconOptions options, MelLogger logger)
{
    try
    {
        var shown = await tray.ShowAsync(options);

        if (shown)
        {
            logger.LogInformation("托盘图标：已显示（右键可退出服务）。");
        }
        else
        {
            logger.LogWarning(
                "托盘图标：显示失败 —— {Reason}（服务继续运行，可用 services.msc / 任务管理器停止）",
                tray.UnavailableReason ?? "原因未知");
        }
    }
    catch (Exception ex)
    {
        // 托盘失败绝不能影响服务本身。
        logger.LogWarning(ex, "托盘图标：显示时发生异常（已忽略）。");
    }
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        // 先摘图标（否则通知区域留个残影），再放单实例锁。
        var tray = app.Services.GetRequiredService<ITrayIconHost>();
        tray.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
    }
    catch
    {
        // 停机路径绝不能再抛异常。
    }

    singleInstanceLock?.Dispose();
});

// 只有"确实还挂着一个控制台"时才设标题：脱离后（默认路径）与双击场景都没有控制台可设。
if (OperatingSystem.IsWindows() && consoleOutputUsable && WindowsInteractiveHost.HasUsableConsole())
{
    try
    {
        Console.Title = $"中控服务 ControlServer — http://localhost:{listeningPort}";
    }
    catch
    {
        // 某些终端不支持设置标题，忽略。
    }
}

Log.Information("监听地址：{Urls}", urls);

// M2-E5 启动自检：确认"可换端口的监听器"真的被 Kestrel 用上了。
// 这条自检的存在意义：接管依赖「Kestrel 会反转 DI 工厂列表」这一**实测出来的**行为
// （见上方 AddSingleton 处的长注释）。将来 .NET 若改了它，这里会立刻报出来，
// 而不是等到用户点"改端口"却发现没反应 —— 那才最难查。
app.Lifetime.ApplicationStarted.Register(() =>
{
    var registry = app.Services.GetRequiredService<ServerAddressRegistry>();

    if (registry.HasListener)
    {
        Log.Information("监听地址热切换：已启用（当前 {Urls}）。", string.Join(", ", registry.Urls));
    }
    else
    {
        Log.Warning(
            "【功能降级】监听地址热切换未启用 —— Kestrel 没有使用可换端口的监听器。"
            + "端口变更仍会写入配置，但需重启服务才能生效。");
    }

    // ── HTTPS 自检（M5.3）──
    //
    // 🔥 必须**在这里**把结论说清楚，而不是等运维去看"为什么没声音"：
    //    https 不可用 → 局域网观看端落入非安全上下文 → WebCodecs / AudioWorklet 不存在
    //    → 远控音频与视频硬解全都用不了，而前后端**一句话都不会报错**。
    //    这是本方案里唯一一处"不报警的整块功能失效"，所以启动时必须有声音。
    var https = app.Services.GetRequiredService<HttpsSetup>();
    var certificates = app.Services.GetRequiredService<HttpsCertificateProvider>();

    if (!https.Enabled)
    {
        Log.Warning(
            "HTTPS 已按配置关闭（Server:Https:Enabled=false）。"
            + "⚠️ 局域网观看端（按 IP 访问 http://…）会落入非安全上下文，"
            + "远控音频与视频硬解在那类观看端上不可用。" + "根证书下载地址：/trust/{FileName}",
            LocalCertificateAuthority.CaDownloadFileName);
    }
    else
    {
        var authority = certificates.Current;

        if (authority is null)
        {
            Log.Error(
                "【功能降级】HTTPS 端点已监听，但本地证书建立失败 —— 所有 https 握手都会失败。"
                + "请到数据目录的 https 子目录检查权限与文件。");
        }
        else
        {
            Log.Information(
                "HTTPS 已就绪：https://0.0.0.0:{Port}｜证书覆盖 {Addresses}｜"
                + "根 CA 指纹 {Fingerprint}｜观看端根证书下载 /trust/{FileName}",
                https.Port,
                authority.CoveredAddresses.Count > 0 ? string.Join("、", authority.CoveredAddresses) : "(只有 localhost)",
                authority.CaFingerprint,
                LocalCertificateAuthority.CaDownloadFileName);

            Log.Information(
                "观看端首次使用请先装一次根证书（下载 /trust/{FileName} → 双击 → "
                + "「安装证书」→ 受信任的根证书颁发机构）。装一次即可，之后换 IP / 换证书都不用再装。",
                LocalCertificateAuthority.CaDownloadFileName);
        }
    }
});
Log.Information("════════ 启动完成 ════════");

// ═══════════════════════════════════════════════════════════════
// 11. 运行（顶层异常给出可读提示，而不是一屏堆栈）
// ═══════════════════════════════════════════════════════════════
try
{
    await app.RunAsync();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    // ⚠️ WinExe 下 Console.Error **没人看得见**（双击启动时连控制台都没有），
    //    所以这里必须同时弹 MessageBox —— 否则用户看到的是"双击了、闪一下、什么都没发生"。
    var portInUseMessage = string.Join(Environment.NewLine, new[]
    {
        "中控服务启动失败：端口被占用。",
        string.Empty,
        $"监听地址：{urls}",
        "可能已经有一个中控服务在运行。",
        string.Empty,
        "处理办法（任选其一）：",
        "  1) 关掉已在运行的那个服务（托盘图标右键 → 退出服务，或 services.msc）；",
        "  2) 在 exe 同目录放一个 appsettings.json，改 Server:Http:Port 与 Server:Listener:PairingPort。",
    });

    Console.Error.WriteLine();
    Console.Error.WriteLine("────────────────────────────────────────────────");
    Console.Error.WriteLine(portInUseMessage);
    Console.Error.WriteLine("────────────────────────────────────────────────");

    Log.Fatal(ex, "【启动失败】端口被占用");

    if (OperatingSystem.IsWindows())
    {
        WindowsInteractiveHost.ShowError("中控服务 · 启动失败", portInUseMessage);
    }

    await Log.CloseAndFlushAsync();
    Environment.ExitCode = 1;
}
catch (Exception ex)
{
    Log.Fatal(ex, "【致命】宿主异常退出");

    // 同上：GUI 子系统下控制台输出不可见，必须有弹窗兜底（非交互式会话里 ShowError 会自行跳过）。
    if (OperatingSystem.IsWindows())
    {
        WindowsInteractiveHost.ShowError(
            "中控服务 · 启动失败",
            $"中控服务启动失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}"
            + $"详细堆栈见日志：{dataDirectory.LogDirectory}");
    }

    await Log.CloseAndFlushAsync();
    Environment.ExitCode = 1;
}

/// <summary>
/// 读 <c>Server:Startup:AttachConsole</c>（开发模式：保留启动终端）。
/// <para>
/// <b>为什么不用 <c>builder.Configuration</c></b>：这个开关必须在
/// <c>WebApplication.CreateBuilder</c> <b>之前</b>就生效（控制台归属决定 Serilog 的
/// Console sink 挂不挂，而 Serilog 在 builder 之后才配置）。所以这里只扫
/// <b>命令行</b>与环境变量这两个"启动即确定"的来源，绝不读文件。
/// </para>
/// <para>
/// 手写扫描而不是 <c>new ConfigurationBuilder().AddCommandLine(args)</c>：
/// 后者对 <c>--key value</c>（空格分隔）会直接抛 FormatException，
/// 而这个开关在启动最早期，任何异常都会变成"连错误提示都发不出来"的静默失败。
/// </para>
/// </summary>
static bool ReadAttachConsoleFlag(string[] argv)
{
    const string key = "Server:Startup:AttachConsole";

    var enabled = false;

    if (bool.TryParse(Environment.GetEnvironmentVariable("Server__Startup__AttachConsole"), out var fromEnv))
    {
        enabled = fromEnv;
    }

    foreach (var argument in argv)
    {
        if (string.IsNullOrEmpty(argument) || argument[0] != '-') continue;

        var text = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : argument[1..];
        var separator = text.IndexOf('=');
        if (separator < 0) continue;

        if (text[..separator].Equals(key, StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(text[(separator + 1)..], out var value))
        {
            enabled = value;
        }
    }

    return enabled;
}

/// <summary>
/// 在资源管理器中打开一个目录。失败只记日志 —— 打不开文件夹绝不该影响服务。
/// </summary>
static void OpenInExplorer(string path, MelLogger logger)
{
    try
    {
        if (!Directory.Exists(path))
        {
            logger.LogWarning("托盘菜单：目录不存在，无法打开 —— {Path}", path);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "托盘菜单：打开目录失败 —— {Path}", path);
    }
}

/// <summary>
/// 把文本放进剪贴板。
/// <para>
/// 走 <c>clip.exe</c> 而不是 <c>System.Windows.Forms.Clipboard</c>：
/// 后者要求 <c>net8.0-windows</c> + <c>UseWindowsForms</c>，会直接违反铁律 #3（详见 WindowsTrayIconHost 注释）。
/// </para>
/// </summary>
static void CopyTextToClipboard(string text, MelLogger logger)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("clip.exe")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
        {
            logger.LogWarning("托盘菜单：clip.exe 未能启动，复制失败。");
            return;
        }

        process.StandardInput.Write(text);
        process.StandardInput.Close();
        logger.LogInformation("托盘菜单：已复制访问地址 {Text}", text);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "托盘菜单：写入剪贴板失败。");
    }
}

/// <summary>
/// 把一份 JSON 配置源放到<b>内置环境变量源之前</b>。
/// <para>
/// <b>为什么是"Add + 挪环境变量"而不是"Insert 到目标位置"</b>：
/// <c>ConfigurationManager.Sources</c> 上的 <c>Insert</c> <b>不会触发 provider 重建</b> ——
/// 源确实进了列表，但本次进程读不到它的任何值（实测：文件里明明写着 8090，
/// <c>Configuration["Server:Http:Urls"]</c> 仍返回 appsettings 的 8899）。
/// 这个坑极其安静：没有异常、没有日志，只是"配置不生效"。
/// 连原本那段"exe 同目录 appsettings.json"（便携部署）也因此<b>从未生效过</b>，
/// 是在做 M2-E5 端口持久化时才被顺带查出来的。
/// </para>
/// <para>
/// 受支持且会触发重建的是 <c>Add</c> / <c>Remove</c>（都实现自 <c>ICollection&lt;T&gt;</c>），
/// 所以改成：先 <c>Add</c> 到末尾，再把<b>环境变量源与命令行源</b>按原相对顺序整体挪到它后面 ——
/// 结果就是"覆盖 appsettings、但不剥夺环境变量与命令行的最高优先级"。
/// </para>
/// <para>
/// <b>曾经的缺陷（2026-10-07 修复）</b>：只挪了环境变量、漏掉了命令行，
/// 于是便携配置反超命令行，<c>--Server:Http:Urls=…</c> / <c>--Server:Storage:DataDir=…</c>
/// 这类参数被<b>静默忽略</b>（服务器照样起在文件里写的 0.0.0.0:8899、数据目录照样是默认值）。
/// 集成测试<b>测不出来</b> —— 它们一律用环境变量注入配置，而那条路径一直是好的。
/// 因此这条优先级规则只能靠真机启动（<c>ControlServer.Server.exe --Server:…</c>）验证。
/// </para>
/// </summary>
static void InsertConfigSourceBeforeEnvironmentVariables(
    ConfigurationManager configuration,
    string directory,
    string fileName)
{
    var sources = configuration.Sources;

    sources.Add(new JsonConfigurationSource
    {
        FileProvider = new PhysicalFileProvider(directory),
        Path = fileName,
        Optional = true,
        ReloadOnChange = false,
    });

    // 按原相对顺序，把「必须高于便携配置」的源整体挪到它后面。
    //
    // ⚠️ 必须同时包含【命令行】源：WebApplicationBuilder 在 CreateBuilder(args) 时
    //    就已经把命令行源注册好了，而 Add 是往末尾追加 —— 只挪环境变量的话，
    //    便携配置会反过来排到命令行【之后】，把 --Server:… 参数静默压掉。
    //    环境变量之间的相对顺序同样要保住（其注册不止一个：DOTNET_ / ASPNETCORE_
    //    前缀的与不带的，同名键的优先级由顺序决定）。
    var higherPriority = sources
        .Where(s => s is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
        .ToList();

    foreach (var source in higherPriority)
    {
        sources.Remove(source);
        sources.Add(source);
    }
}

/// <summary>
/// 模型状态里是否记录了「multipart 请求体超过上限」这一次失败。
/// <para>
/// 只能靠框架给出的**英文文案**来识别（<c>Multipart body length limit … exceeded</c>）：
/// 这条错误由 MVC 的 <c>FormValueProviderFactory</c> 写入，没有专门的错误码，
/// 也拿不到强类型的异常对象。因此同时接受"异常本身是 <see cref="InvalidDataException"/>"这一形态，
/// 两道判据任一命中即认为超限。
/// </para>
/// <para>
/// 代价是它依赖框架文案 —— 若将来 .NET 改了措辞，表现会**回退**成框架默认的 400，
/// 而不会变成错误的行为。集成测试 <c>Upload_rejects_a_body_larger_than_the_multipart_buffer_limit</c>
/// 就是这条依赖的哨兵。
/// </para>
/// </summary>
static bool IsMultipartBodyTooLarge(ModelStateDictionary modelState)
{
    foreach (var entry in modelState.Values)
    {
        foreach (var error in entry.Errors)
        {
            if (error.Exception is InvalidDataException) return true;

            if (error.ErrorMessage?.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }
    }

    return false;
}

/// <summary>判断异常链里是否含"地址已被占用"（Kestrel 绑定失败）。</summary>
static bool IsAddressInUse(Exception exception)
{
    for (Exception? e = exception; e is not null; e = e.InnerException)
    {
        if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
        {
            return true;
        }

        if (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

/// <summary>
/// "必须先改密"的账号仍被允许访问的端点。
/// <para>
/// 必须包含 <c>change-password</c> 本身 —— 否则用户会卡在
/// "要先改密"与"改密接口不允许访问"之间的死结，且没有任何自救路径。
/// <c>me</c> / <c>logout</c> 是前端维持会话与登出的最小需要。
/// </para>
/// </summary>
static bool IsPasswordChangeAllowlisted(PathString path)
    => path.StartsWithSegments("/api/auth/change-password")
        || path.StartsWithSegments("/api/auth/me")
        || path.StartsWithSegments("/api/auth/logout");

/// <summary>供集成测试使用：<c>WebApplicationFactory&lt;Program&gt;</c> 需要 Program 可访问。</summary>
public partial class Program
{
}
