namespace ControlServer.Agent.Config;

/// <summary>
/// <b>这台机器要不要强制走 MJPEG（不用 GPU 硬编）</b>（M5.4-b）。
///
/// <para>
/// 🔥 <b>它为什么必须存在</b>：GPU 那条路是"抓屏 → 色彩转换 → 硬编"三件事串在一条
/// D3D11 链子上，任何一环在某个具体驱动/机型上出问题，表现都是<b>画面根本出不来</b>。
/// 而现场（展厅）没有人能改代码、也没有人愿意等一版补丁 —— 他们需要的是
/// <b>一个能立刻把画面拿回来的开关</b>。MJPEG 那条路是软的、无依赖的、被 M5.1 验证过的，
/// 它就是那个"保底能看见画面"的档。
/// </para>
///
/// <para>
/// 🔥 <b>与"自动回退"是两件事，两个都要有</b>：
/// </para>
/// <list type="bullet">
///   <item><b>自动回退</b>（建不起来就换 MJPEG）处理的是"这台机器<b>做不了</b>"
///         —— 例如没硬编、驱动不认、屏被别的抓屏程序占着。它不需要人介入。</item>
///   <item><b>这个开关</b>处理的是"这台机器<b>做起来有问题</b>"
///         —— 能建起来、能出画面，但现场就是不对劲（花屏、卡顿、颜色怪、指针位置偏）。
///         这类问题的判定权<b>必须在现场的人手里</b>：他看得见画面，我们看不见。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>做法刻意与 <see cref="AgentInputPolicy"/> 完全一致</b>（读文件、每次开流问一次、
/// 不走中心下发），理由也一样：它要能在<b>不重启托盘</b>的前提下立即生效，
/// 而且<b>中心不该有能力把这条保底路关掉</b> —— 那等于把"最后一条能看见画面的路"也交出去了。
/// </para>
/// </summary>
public static class AgentGpuPolicy
{
    /// <summary>
    /// 强制 MJPEG 的标记文件名。<b>它存在 = 不用 GPU 硬编。</b>
    /// </summary>
    /// <remarks>
    /// ⚠️ 名字里<b>不出现</b>「deny / disable」这类词：这条规矩是"用哪条路"，
    /// 不是"禁用什么"。取反关系只写在下面 <see cref="ForcesMjpegIn"/> 那一行
    /// （见 <see cref="AgentInputPolicy.AllowsInputIn"/> 的同款说明）。
    /// </remarks>
    public const string ForceMjpegFileName = "remote-video.mjpeg";

    /// <summary>标记文件的完整路径（排障时要能打进日志，不能让人猜目录）。</summary>
    public static string ForceMjpegPath(string stateDirectory) =>
        Path.Combine(stateDirectory, ForceMjpegFileName);

    /// <summary>
    /// 一次性判断：<b>此刻这台机器要不要强制 MJPEG</b>。不缓存、不记日志。
    /// </summary>
    /// <remarks>
    /// 🔥 <b>整个项目里"文件在 = 不用硬编"这个判断只写在这一行</b>。
    /// 它写反了不会报任何错，只会让这个开关彻底失效（甚至反过来：建了文件反而更想用硬编）——
    /// 所以它必须只有一个出处。
    /// </remarks>
    public static bool ForcesMjpegIn(string stateDirectory) => File.Exists(ForceMjpegPath(stateDirectory));
}
