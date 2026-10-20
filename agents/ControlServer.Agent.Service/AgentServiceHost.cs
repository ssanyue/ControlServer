using System.Diagnostics;
using ControlServer.Contracts;
using ControlServer.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace ControlServer.Agent.Service;

/// <summary>
/// 把 <see cref="AgentRuntime"/> 的主循环接到宿主生命周期上（M4.1）。
/// <para>
/// 抽成 HostedService 而不是在 <c>Program.cs</c> 里裸跑 <c>Task.Run</c>：
/// 这样停机时宿主会<b>等它收尾</b>（断开长连接、落盘状态），而不是把进程直接掐掉 ——
/// 后者会让中心服务端那边多等一个心跳超时才变离线。
/// </para>
/// </summary>
public sealed class AgentServiceHost : BackgroundService
{
    private readonly AgentRuntime _runtime;
    private readonly AgentOptions _options;
    private readonly LogService _log;
    private readonly IHostApplicationLifetime _lifetime;

    public AgentServiceHost(
        AgentRuntime runtime,
        AgentOptions options,
        LogService log,
        IHostApplicationLifetime lifetime)
    {
        _runtime = runtime;
        _options = options;
        _log = log;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 先订阅再开跑：反过来的话，"第一条指令刚好在订阅建立之前到达"会被永久漏掉，
        // 而且没有任何日志能说明发生了什么。
        _runtime.CommandReceived += OnCommand;

        try
        {
            await _runtime.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Agent", "客户端主循环异常终止", ex);
        }
        finally
        {
            _runtime.CommandReceived -= OnCommand;
        }
    }

    /// <summary>
    /// 处理一条服务端指令。
    /// <para>
    /// 🔥 <b>这里只留「宿主自己才干得了」的那一条</b> —— <c>restart</c>（把本进程重新拉起来）。
    /// 「关机 / 重启整机」与「音量」由 <see cref="AgentRuntime"/> 交给
    /// <c>AgentCommandExecutor</c> 就地执行（2026-10-15 修），<b>不经过这里</b>。
    /// </para>
    /// <para>
    /// 为什么必须划清：这两类指令曾经**都**落到这里，而这里的 switch 不认它们 ⇒
    /// 落到 <c>default</c> ⇒ 「收到未知指令（已忽略）」——
    /// 页面上的关机 / 重启因此完全没有效果（UDP 那条路却正常，因为它的实现在别处）。
    /// 所以<b>不要在这里补电源分支</b>：那会让两条通道各有一份执行，迟早只补一边。
    /// </para>
    /// </summary>
    private void OnCommand(AgentCommandDto command)
    {
        switch (command.Name)
        {
            case AgentCommandNames.Restart:
                RestartSelf();
                break;

            case AgentCommandNames.RefreshConfig:
                // M4.5 起「重新拉取配置」由 AgentRuntime 直接处理（它才握着凭据与长连接），
                // 这里只是把"收到了"记一行 —— 免得现场看到日志里没有这一条而怀疑指令没到。
                _log.Info("Agent", "收到「重新拉取配置」指令（已由主循环处理，正在强制拉取全量配置）。");
                break;

            default:
                _log.Warn("Agent", $"收到未知指令：{command.Name}（已忽略）");
                break;
        }
    }

    /// <summary>
    /// 远程重启自己。
    /// <para>
    /// <b>为什么不是"直接退出然后等服务恢复"</b>：Windows 服务的"失败恢复"默认是<b>不重启</b>，
    /// 直接退出会表现为"重启一次就再也起不来了"，比不重启更糟。
    /// 这里起一个<b>分离的</b>命令行进程：等本进程退出后再 <c>sc start</c>，
    /// 这样不依赖任何服务恢复策略。
    /// </para>
    /// <para>
    /// 服务名必须与实际注册的一致 —— 由 <c>Agent:ServiceName</c> 配置，安装脚本按它写死。
    /// </para>
    /// </summary>
    private void RestartSelf()
    {
        var serviceName = string.IsNullOrWhiteSpace(_options.ServiceName)
            ? "ControlServerAgent"
            : _options.ServiceName.Trim();

        if (!WindowsServiceHelpers.IsWindowsService())
        {
            // 前台运行时"重启自己"没有意义（没有服务可 start），只提示一句。
            _log.Warn("Agent", "当前不是服务形态，忽略重启指令（前台运行请手工重启）。");
            return;
        }

        _log.Info("Agent", $"执行重启指令：3 秒后重新启动服务 {serviceName}。");

        try
        {
            // `timeout` 提供这 3 秒的等待；此时本进程正在优雅停机，随后 `sc start` 才可能成功。
            var psi = new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & sc start {serviceName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.Error("Agent", "安排重启失败（服务将保持停止，需手工启动）", ex);
            return;
        }

        _lifetime.StopApplication();
    }
}
