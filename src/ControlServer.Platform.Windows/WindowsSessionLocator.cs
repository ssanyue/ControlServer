using System.Runtime.Versioning;

namespace ControlServer.Platform.Windows;

/// <summary>
/// 找出"用户桌面此刻在哪个会话"（M4.5）。
///
/// <para>
/// <b>为什么需要它</b>：客户端服务跑在 Session 0，而托盘跑在用户的交互式会话里，
/// 两者之间靠<b>命名管道</b>通信。管道名按会话编号区分（见 <c>SessionPipeProtocol</c>），
/// 所以服务必须能回答"我该去敲哪个会话的门"。
/// </para>
///
/// <para>
/// 🔥 <b>为什么按会话编号区分管道名，而不是全局共用一个名字</b>：
/// 托盘的"单实例"互斥量是 <c>Local\</c> 作用域（<b>每会话一个</b>），
/// 也就是说多用户同时登录（快速用户切换 / 远程桌面）时会有<b>多个托盘同时运行</b>。
/// 若共用一个管道名，只有先到的那个能把管道建起来，服务从此就固定敲到<b>先登录的那个人的桌面</b>上 ——
/// 音量改在别人的账户里，而日志上完全看不出异常。
/// 按会话编号命名之后，"改哪个桌面的音量"变成一件可解释、可排查的事。
/// </para>
///
/// <para>
/// ⚠️ <b>覆盖范围要说清</b>：本方法取的是<b>控制台会话</b>（物理显示器前的那个人）。
/// 纯远程桌面（无人在控制台）的场景下它可能取不到活动会话 —— 那种情况下
/// 会话侧指令会明确报告"当前没有交互式控制台会话"，而不是随便挑一个会话乱改。
/// 展厅机器的用户就在控制台前，这正是本方法对准的场景。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSessionLocator
{
    /// <summary><c>WTSGetActiveConsoleSessionId</c> 的"没有会话"返回值。</summary>
    private const uint NoSession = 0xFFFFFFFF;

    /// <summary>
    /// 取当前控制台会话的编号（<b>没有交互式会话时返回 null</b>）。
    /// <para>任何异常都归为 null —— 这个调用只用于"能不能找到用户的桌面"，
    /// 它失败时该有的表现是"通道暂时不可用"，绝不是让调用方崩掉。</para>
    /// </summary>
    public static int? TryGetActiveConsoleSessionId()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var id = NativeMethods.WTSGetActiveConsoleSessionId();

            return id == NoSession ? null : (int)id;
        }
        catch
        {
            return null;
        }
    }
}
