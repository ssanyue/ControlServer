using System.Diagnostics;
using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;

// ═══════════════════════════════════════════════════════════════════════════
// 中控服务 · 托盘助手（方案 D）
//
// 为什么需要它：Windows 服务跑在 **Session 0**，那里没有 explorer、没有通知区域，
// 所以**服务进程自己不可能显示托盘图标**（这不是实现问题，是会话模型）。
// 于是把托盘放到"用户会话"里，做成这个独立小进程。
//
// 设计原则（方案 §11.2）：**职责刻意做小**，只做 4 件事：
//   1. 显示图标（含"服务未运行"的状态）
//   2. 打开管理界面（浏览器，零凭据）
//   3. 启动 / 停止服务（走 SCM，见 §11.3 的 D1，零自研 IPC）
//   4. 探活（GET /healthz，匿名端点）
// 不读配置、不读日志内容、不调管理 API、不传凭据 —— 这样与主服务**版本不一致也不会坏**。
//
// 启动方式：安装脚本为**当前登录用户**注册启动项（HKCU\...\Run，方案 §11.4 的 D-a），
// 因此每个要用托盘的账号装一次，且全程**不需要管理员权限**。
// ═══════════════════════════════════════════════════════════════════════════

const string DefaultServiceName = "ControlServer";
const int DefaultPort = 8899;

var serviceName = GetArgument("--service-name") ?? DefaultServiceName;
var port = int.TryParse(GetArgument("--port"), out var parsedPort) ? parsedPort : DefaultPort;
var dataRoot = GetArgument("--data-dir")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ControlServer");

Log($"启动：服务名={serviceName} 端口={port} 数据目录={dataRoot}");

if (!OperatingSystem.IsWindows())
{
    Log("非 Windows 平台，退出。");
    return;
}

// ── 单实例：任务栏里只应有一个助手图标 ──
using var singleInstance =
    WindowsInteractiveHost.TryAcquireSingleInstance(@"Local\ControlServer.TrayAgent.SingleInstance");

if (singleInstance is null)
{
    Log("已有一个托盘助手在运行，本次退出。");
    return;
}

var controller = new WindowsServiceController(serviceName);

await using var tray = new WindowsTrayIconHost();

if (!tray.IsAvailable)
{
    Log($"托盘不可用：{tray.UnavailableReason}");

    WindowsInteractiveHost.ShowError(
        "中控服务 · 托盘助手",
        $"无法显示托盘图标：{tray.UnavailableReason}{Environment.NewLine}{Environment.NewLine}"
        + "（服务仍在运行；可用 services.msc 或 `sc stop ControlServer` 停止。）");

    return;
}

// ⚠️ 先取初始状态再挂命令处理器：处理器里的 Refresh 要捕获 lastState，
//    而 C# 不允许引用"声明在之后"的局部变量。
var initialState = controller.QueryStatus();
var lastState = initialState.State;

Log($"初始服务状态：{initialState.Describe()}（{initialState.ErrorMessage ?? "无错误"}）");

var exitSignal = new TaskCompletionSource();

tray.CommandInvoked += commandId =>
{
    try
    {
        switch (commandId)
        {
            case TrayCommands.OpenConsole:
                OpenUrl($"http://localhost:{port}/");
                break;

            case TrayCommands.StartService:
                HandleServiceAction("启动", controller.Start());
                break;

            case TrayCommands.StopService:
                HandleServiceAction("停止", controller.Stop());
                break;

            case TrayCommands.OpenDataDirectory:
                OpenInExplorer(dataRoot);
                break;

            case TrayCommands.OpenLogDirectory:
                OpenInExplorer(Path.Combine(dataRoot, "logs"));
                break;

            case TrayCommands.Exit:
                Log("用户选择「退出托盘助手」。");
                exitSignal.TrySetResult();
                break;

            case TrayCommands.StatusInfo:
            default:
                // 纯信息行（不可点）或未知命令：忽略。
                break;
        }
    }
    catch (Exception ex)
    {
        Log($"处理命令 {commandId} 时异常：{ex}");
    }

    Refresh(force: true);
};

// ── 显示图标 ──
if (!await tray.ShowAsync(BuildOptions(initialState)))
{
    Log($"托盘图标显示失败：{tray.UnavailableReason}");

    WindowsInteractiveHost.ShowError(
        "中控服务 · 托盘助手",
        $"托盘图标显示失败：{tray.UnavailableReason}");

    return;
}

// 首次提示一次，让用户知道图标在哪、能干什么。
_ = tray.ShowBalloonAsync("中控服务", $"托盘助手已启动 · {initialState.Describe()}");

// ── 轮询服务状态；状态真变时才刷新菜单 ──
var tick = TimeSpan.FromSeconds(3);

while (true)
{
    var finished = await Task.WhenAny(exitSignal.Task, Task.Delay(tick));
    if (finished == exitSignal.Task) break;

    Refresh();
}

await tray.DisposeAsync();
Log("托盘助手已退出。");

// ═══════════════════════════════════════════════════════════════════════════
// 本地函数
// ═══════════════════════════════════════════════════════════════════════════

static string? GetArgument(string name)
{
    var args = Environment.GetCommandLineArgs();

    for (var i = 1; i < args.Length; i++)
    {
        // 同时接受 "--key value" 与 "--key=value"。
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        var prefix = name + "=";
        if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return args[i][prefix.Length..];
        }
    }

    return null;
}

TrayIconOptions BuildOptions(WindowsServiceStatus status)
{
    var running = status.IsRunning;
    var stopped = status.State == WindowsServiceState.Stopped;
    var pending = status.IsPending;

    var menu = new List<TrayMenuCommand>();

    if (status.State == WindowsServiceState.NotInstalled)
    {
        // 没装服务时先告诉用户"该做什么"，而不是给一排点不动的灰项。
        menu.Add(new TrayMenuCommand("未安装为服务", TrayCommands.StatusInfo, Enabled: false));
        menu.Add(new TrayMenuCommand(
            @"请先运行 deploy\windows\install-service.ps1", TrayCommands.StatusInfo, Enabled: false));
    }
    else
    {
        menu.Add(new TrayMenuCommand($"服务状态：{status.Describe()}", TrayCommands.StatusInfo, Enabled: false));
    }

    menu.Add(TrayMenuCommand.Separator);
    menu.Add(new TrayMenuCommand("打开管理界面", TrayCommands.OpenConsole, Enabled: running, IsDefault: true));
    menu.Add(new TrayMenuCommand("启动服务", TrayCommands.StartService, Enabled: stopped && !pending));
    menu.Add(new TrayMenuCommand("停止服务", TrayCommands.StopService, Enabled: running && !pending));
    menu.Add(TrayMenuCommand.Separator);
    menu.Add(new TrayMenuCommand("打开数据目录", TrayCommands.OpenDataDirectory));
    menu.Add(new TrayMenuCommand("打开日志目录", TrayCommands.OpenLogDirectory));
    menu.Add(TrayMenuCommand.Separator);
    menu.Add(new TrayMenuCommand("退出托盘助手", TrayCommands.Exit));

    return new TrayIconOptions(
        ToolTip: $"中控服务 · {serviceName} — {status.Describe()}",
        Menu: menu,
        DefaultCommandId: TrayCommands.OpenConsole);
}

void Refresh(bool force = false)
{
    var status = controller.QueryStatus();

    if (!force && status.State == lastState) return;

    lastState = status.State;
    _ = tray.UpdateAsync(BuildOptions(status));

    if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
    {
        Log($"状态查询告警：{status.ErrorMessage}");
    }
}

void HandleServiceAction(string verb, string? error)
{
    if (error is null)
    {
        Log($"{verb}服务：已发出请求。");
        return;
    }

    Log($"{verb}服务失败：{error}");

    WindowsInteractiveHost.ShowError(
        $"中控服务 · {verb}失败",
        $"无法{verb}服务「{serviceName}」。{Environment.NewLine}{Environment.NewLine}{error}");
}

static void OpenUrl(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log($"打开 URL 失败：{ex.Message}");
    }
}

static void OpenInExplorer(string path)
{
    try
    {
        if (!Directory.Exists(path))
        {
            Log($"目录不存在，无法打开：{path}");
            WindowsInteractiveHost.ShowError("中控服务 · 托盘助手", $"目录不存在：{path}");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Log($"打开目录失败：{ex.Message}");
    }
}

/// <summary>
/// 极简文件日志。
/// <para>
/// 托盘助手是 WinExe（没有控制台），所以**必须**留下可查的痕迹 ——
/// 否则"托盘没出现"这类故障无从下手。写到 %TEMP% 而不是数据目录：
/// 数据目录可能由服务（SYSTEM）创建、普通用户没有写权限。
/// </para>
/// </summary>
static void Log(string message)
{
    try
    {
        var path = Path.Combine(Path.GetTempPath(), "ControlServer.TrayAgent.log");
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";
        File.AppendAllText(path, line);
    }
    catch
    {
        // 记日志失败不该影响助手本身。
    }
}
