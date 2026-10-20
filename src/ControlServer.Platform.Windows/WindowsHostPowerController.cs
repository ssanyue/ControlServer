using System.Diagnostics;
using System.Runtime.Versioning;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Windows;

/// <summary>
/// Windows 上的电源控制（M4.5）：调系统自带的 <c>shutdown.exe</c>。
///
/// <para><b>为什么用 <c>shutdown.exe</c> 而不是 P/Invoke <c>ExitWindowsEx</c></b>：</para>
/// <list type="bullet">
///   <item><c>ExitWindowsEx(EWX_SHUTDOWN)</c> 只是"通知系统准备关机"，<b>不会真的断电</b>
///         （它等价于按下"关机"按钮后那个等待态），要真断电还得自己调
///         <c>SetSystemPowerState</c> 或 <c>InitiateSystemShutdown</c>；
///         <c>shutdown.exe</c> 一把就把这件事做对了。</item>
///   <item><c>-t</c> 的延时期内可以用 <c>shutdown /a</c> <b>撤销</b> —— 这是个真实有用的安全阀
///         （误发指令后还有几秒可以救回来），P/Invoke 那条路要自己实现。</item>
///   <item>没有 P/Invoke ⇒ 没有结构体布局、没有 <c>SE_SHUTDOWN_NAME</c> 特权开启失败这类
///         只能靠现场复现的问题。</item>
/// </list>
///
/// <para><b>权限</b>：作为 Windows 服务的 SYSTEM 天然持有 <c>SeShutdownPrivilege</c>。
/// 前台以普通用户调试时可能失败，此时会把 <c>shutdown.exe</c> 的退出码原样写进日志
/// （5 = 拒绝访问），而不是笼统的"失败了"。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostPowerController : IHostPowerController
{
    /// <summary>
    /// 延时（秒）。<b>刻意不是 0</b>：
    /// <list type="bullet">
    ///   <item>日志要有时间落盘 —— "收到了关机指令"是事后唯一能查到的证据；</item>
    ///   <item>用户会话里的程序能收到关机通知并保存数据，而不是被硬切；</item>
    ///   <item>误发时还有 <c>shutdown /a</c> 的机会。</item>
    /// </list>
    /// </summary>
    public const int DelaySeconds = 5;

    /// <summary>等待 <c>shutdown.exe</c> 自己退出的上限。<b>必须设</b>：它理论上会立刻返回，但不能赌。</summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string AvailabilityNote => IsAvailable ? string.Empty : "当前平台不是 Windows。";

    /// <inheritdoc />
    public Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
        => LaunchAsync("/s", "关机", reason, cancellationToken);

    /// <inheritdoc />
    public Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default)
        => LaunchAsync("/r", "重启", reason, cancellationToken);

    private async Task<HostPowerResult> LaunchAsync(
        string action,
        string actionName,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return HostPowerResult.Failure($"本平台不支持电源控制：{AvailabilityNote}");
        }

        var arguments = $"{action} /t {DelaySeconds}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return HostPowerResult.Failure("无法启动 shutdown.exe（返回了空进程句柄）。");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LaunchTimeout);

            string? stderr = null;

            try
            {
                stderr = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时：命令可能已经生效了（只是那个进程没退），所以不能断言失败。
                return HostPowerResult.Failure(
                    $"shutdown.exe 在 {LaunchTimeout.TotalSeconds:0} 秒内没有返回（指令可能已经生效，请以实际是否关机为准）。");
            }

            if (process.ExitCode == 0)
            {
                var suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"（{reason.Trim()}）";

                return HostPowerResult.Success(
                    $"已安排 {actionName}{suffix}：{DelaySeconds} 秒后执行，期间可用 shutdown /a 撤销。");
            }

            var detail = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"：{stderr.Trim()}";

            return HostPowerResult.Failure(
                $"shutdown.exe {arguments} 失败，退出码 {process.ExitCode}{detail}"
                + (process.ExitCode == 5 ? "（拒绝访问：本进程没有关机权限）" : string.Empty));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 铁律 #6：执行器不抛，一律转成失败结果（调用方只需看 Ok）。
            return HostPowerResult.Failure($"调用 shutdown.exe 出错：{ex.Message}");
        }
    }
}
