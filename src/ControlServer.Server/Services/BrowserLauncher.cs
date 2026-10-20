using System.Diagnostics;

namespace ControlServer.Server.Services;

/// <summary>
/// 用系统默认浏览器打开本机管理界面。
///
/// 设计约束（"一定要稳定"）：
///   1. 打不开浏览器【绝不能】影响服务运行 —— 全部异常吞掉并降级为告警日志；
///   2. 服务形态（Windows 服务 / systemd）下静默跳过 —— Session 0 没有用户桌面；
///   3. 只在交互式会话中尝试。
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>
    /// 尝试打开浏览器。永不抛异常。
    /// </summary>
    /// <param name="port">本机 HTTP 端口。</param>
    /// <param name="enabled">配置开关（Server:Startup:AutoOpenBrowser）。</param>
    /// <param name="isServiceMode">是否运行在服务宿主形态下。</param>
    /// <param name="logger">日志。</param>
    public static void TryOpen(int port, bool enabled, bool isServiceMode, ILogger logger)
    {
        if (!enabled)
        {
            logger.LogInformation("自动打开浏览器：配置已关闭（Server:Startup:AutoOpenBrowser=false）");
            return;
        }

        if (isServiceMode || !Environment.UserInteractive)
        {
            logger.LogInformation("自动打开浏览器：当前为非交互式会话（服务形态），已跳过");
            return;
        }

        // 监听在 0.0.0.0 上，本机访问用 localhost 最稳妥。
        var url = $"http://localhost:{port}/";

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });

            logger.LogInformation("已请求系统默认浏览器打开：{Url}", url);
        }
        catch (Exception ex)
        {
            // 桌面环境缺失、无默认浏览器、被策略拦截……都不该拖垮服务。
            logger.LogWarning(ex, "自动打开浏览器失败（服务继续正常运行），请手动访问 {Url}", url);
        }
    }
}
