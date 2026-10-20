using System.ComponentModel;
using System.Diagnostics;
using ControlServer.Contracts;

namespace ControlServer.Agent.Tasks;

/// <summary>
/// 在<b>用户会话里</b>启动一个程序（M4.5，「延时启动程序」的落地动作）。
///
/// <para>
/// 🔥 <b>为什么必须由托盘来做</b>：服务在 Session 0 里 <c>Process.Start</c> 出来的进程
/// 属于 Session 0 —— 它没有桌面，窗口<b>一个像素都不会出现在用户屏幕上</b>，
/// 但 <c>Process.Start</c> 会如实返回"启动成功"。这类"成功但看不见"的失败
/// 是本项目最忌讳的形态，所以这件事只能在用户的会话里做。
/// </para>
///
/// <para><b>为什么用 <c>UseShellExecute = true</c></b>：走 ShellExecute 与
/// "用户在资源管理器里双击这个文件"完全等价 —— 目标程序拿到的是<b>用户</b>的
/// 环境变量、用户配置文件目录与交互式桌面，而不是托盘的继承环境。
/// 现场配置里写 <c>.bat</c> / <c>.lnk</c> / 文档路径的情形也因此一并支持。</para>
///
/// <para><b>为什么不在这里加"可执行文件白名单"</b>：任务内容由管理员在中心网页上配置，
/// 执行身份就是当前登录用户（<b>没有提权</b>）。能改中心配置的人本来就能对全部客户机
/// 为所欲为，在托盘这一层再拦一道只会制造"网页上配了却不生效"的新谜题。
/// 真正的边界在服务端 —— 那条链路上有登录、有 RBAC、有授权闸门。</para>
/// </summary>
public static class ProgramLauncher
{
    /// <summary>启动一个程序。任何失败都转成 <see cref="SessionCommandResult.Failure"/>，<b>不抛</b>。</summary>
    public static SessionCommandResult Launch(AgentLaunchTaskDto task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var path = task.ProgramPath?.Trim();

        if (string.IsNullOrEmpty(path))
        {
            return SessionCommandResult.Failure("没有填写程序路径。");
        }

        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = task.Argument ?? string.Empty,
            };

            // ── 工作目录 ──
            // ⚠️ 不设它的话，ShellExecute 出来的进程工作目录可能是 C:\Windows\System32，
            //    于是"程序找不到自己旁边的配置文件"，而现象是"程序起来了但行为不对"，
            //    与"启动失败"看起来完全是两码事。
            //    设为程序所在目录 = 与在资源管理器里双击它一致。
            if (Path.IsPathRooted(path))
            {
                var directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    info.WorkingDirectory = directory;
                }
            }

            using var process = Process.Start(info);

            if (process is null)
            {
                // UseShellExecute 路径上极少数情况下会返回 null（拿不到进程对象）。
                // 命令其实已经发出去了，但既然拿不到凭据，就不该谎报"已启动"。
                return SessionCommandResult.Failure("系统没有返回进程对象（命令可能已发出，但无法确认）。");
            }

            int? pid = null;

            try
            {
                pid = process.Id;
            }
            catch
            {
                // 目标程序可能已经退出，Id 拿不到不影响"确实拉起来了"这个结论。
            }

            var display = string.IsNullOrWhiteSpace(task.Name) ? path : task.Name.Trim();

            return SessionCommandResult.Success(
                pid is { } value
                    ? $"{display} 已在用户会话中启动（PID {value}）。"
                    : $"{display} 已在用户会话中启动。");
        }
        catch (Win32Exception ex)
        {
            // 最常见的是 ERROR_FILE_NOT_FOUND（路径不存在）与 ERROR_ACCESS_DENIED。
            // 把系统错误码一起给出来 —— 现场靠它一眼分清"路径写错了"与"权限不够"。
            return SessionCommandResult.Failure($"启动失败：{ex.Message}（系统错误 {ex.NativeErrorCode}）");
        }
        catch (Exception ex)
        {
            return SessionCommandResult.Failure($"启动失败：{ex.Message}");
        }
    }
}
