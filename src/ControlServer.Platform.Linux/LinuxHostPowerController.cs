using System.Diagnostics;
using ControlServer.Platform.Abstractions;

namespace ControlServer.Platform.Linux;

/// <summary>
/// Linux 上的整机关机 / 重启（M7.2）：走 <c>systemctl poweroff</c> / <c>systemctl reboot</c>。
///
/// <para>
/// 🔥 <b>为什么不用 <c>shutdown -h now</c></b>：那个命令在 systemd 发行版上是
/// <c>systemctl</c> 的软链接，但在 Alpine / 容器里可能指向 busybox 的另一套语义；
/// 而 <c>systemctl</c> 是 systemd 系的**规范入口**，失败时还会给出可读的原因
/// （"Call to PowerOff failed: Access denied" 这句能直接告诉现场"是权限不够"）。
/// </para>
///
/// <para>
/// ⚠️ <b>「已发起」≠「已关机」</b>（与 Windows 实现同一条约定）：
/// 命令会先广播给所有会话、让服务有序停止，真正的断电在几秒之后。
/// 返回 <c>true</c> 只意味着 systemd 接受了这个请求。
/// </para>
///
/// <para>
/// ⚠️ <b>需要权限</b>：非 root 且不在 <c>wheel</c> / <c>sudo</c> 白名单里的进程会被 polkit 拒绝。
/// 那时返回的是**原始 stderr**（不是一句"失败了"）—— 因为"权限不够"与"systemd 没跑"
/// 的处置完全不同，而这两句在笼统的失败信息里长得一样。
/// </para>
/// </summary>
public sealed class LinuxHostPowerController : IHostPowerController
{
    private const int CommandTimeoutMs = 5000;

    private readonly string? _systemctl;

    public LinuxHostPowerController()
    {
        _systemctl = FindExecutable("systemctl");

        IsAvailable = _systemctl is not null;

        AvailabilityNote = _systemctl is null
            ? "本机没有 systemctl（不是 systemd 系统，例如 Alpine / 容器）—— 这里不能关机 / 重启。"
            : string.Empty;
    }

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public string AvailabilityNote { get; }

    /// <inheritdoc />
    public Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
        => RunPowerAsync("poweroff", reason, cancellationToken);

    /// <inheritdoc />
    public Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default)
        => RunPowerAsync("reboot", reason, cancellationToken);

    private Task<HostPowerResult> RunPowerAsync(string verb, string? reason, CancellationToken cancellationToken)
    {
        if (!IsAvailable || _systemctl is null)
        {
            return Task.FromResult(HostPowerResult.Failure($"本机无法执行 {verb}：{AvailabilityNote}"));
        }

        return Task.Run(
            () =>
            {
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = _systemctl,

                            // ⚠️ `--no-block`：不要在这里等 systemd 把整台机器停完 ——
                            //    那要好几秒，而我们的调用方（UDP 指令通道）还得先把回执发出去。
                            //    非阻塞之后"已发起"的语义才与 Windows 那条一致。
                            Arguments = $"{verb} --no-block",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        },
                    };

                    if (!process.Start())
                    {
                        return HostPowerResult.Failure($"没能启动 {_systemctl} {verb}。");
                    }

                    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(CommandTimeoutMs))
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch (Exception)
                        {
                            // 忽略：超时这句话本身已经是结论。
                        }

                        return HostPowerResult.Failure(
                            $"{_systemctl} {verb} 超过 {CommandTimeoutMs} 毫秒没有返回（systemd 可能卡住了）。");
                    }

                    if (process.ExitCode != 0)
                    {
                        // 🔥 原样带回 stderr：polkit 拒绝与"没有 systemd"要靠这句话区分。
                        return HostPowerResult.Failure(
                            $"{_systemctl} {verb} 失败（退出码 {process.ExitCode}）：{FirstLine(output)}");
                    }

                    var reasonText = string.IsNullOrWhiteSpace(reason) ? "（没有说明）" : reason;

                    return HostPowerResult.Success(
                        $"已请 systemd 执行 {verb}（原因：{reasonText}）。断电/重启会在几秒后发生 —— "
                        + "这条回执只代表它被接受了。");
                }
                catch (Exception ex)
                {
                    return HostPowerResult.Failure($"执行 {verb} 时出错：{ex.Message}");
                }
            },
            cancellationToken);
    }

    private static string? FindExecutable(string name)
    {
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" })
        {
            var path = Path.Combine(dir, name);

            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return string.IsNullOrWhiteSpace(line) ? "（没有输出）" : line.Trim();
    }
}
