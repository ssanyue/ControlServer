namespace ControlServer.Platform.Abstractions;

/// <summary>
/// 被控端的<b>唤醒锁</b>：在有人看这一路远控的期间，阻止系统进入
/// 「关闭显示器 / 屏幕保护 / 睡眠」。
///
/// <para>
/// 🔥 <b>它治的是一个会被误判成"抓屏坏了"的现象</b>：一台没人碰的机器静置几分钟后，
/// 系统按计划关掉了显示器（或起了黑屏屏保）。此时 DDA 仍然能抓到东西 ——
/// 抓到的就是<b>一片纯黑</b>，而且真的有帧、真的在编码、真的在传，
/// 只是内容全黑。现场看到的形态是：
/// </para>
/// <list type="bullet">
///   <item>画面全黑，但<b>鼠标动一下（或被控端有任何本地输入）就恢复正常</b>；</item>
///   <item>单帧极小（4K 的 IDR 只有 2~3 KB —— 纯色才编得出这么小）；</item>
///   <item>两端日志干干净净，没有任何错误。</item>
/// </list>
///
/// <para>
/// ⚠️ <b>它是"预防"，不是"急救"</b>：拿锁<b>不会</b>退出已经起来的屏保
/// （屏保按设计不接受模拟输入），只能保证"从现在起别再息屏"。
/// 已经黑的屏由实现里的"点亮一次"负责，而<b>屏保只能由现场关掉</b>。
/// </para>
///
/// <para>
/// ⚠️ <b>代价要摆明</b>：持有期间被控端显示器<b>不会自动关闭</b>。
/// 一台展厅机器被远程挂着一整晚，屏就一直亮着 —— 这是这个能力的固有成本，
/// 所以它必须能被配置关掉，而不是写死。
/// </para>
///
/// <para>
/// 🔴 <b>所有方法都不抛</b>：调用点在推流循环的启动/收尾路径上，
/// 那里抛异常的表现是"会话建起来了、画面不动"，比不做这件事更糟。
/// </para>
/// </summary>
public interface IDisplayWakeLock : IDisposable
{
    /// <summary>
    /// 此刻是否<b>真的</b>持有（实现必须回答"持有线程还活着"，而不是"我们曾经申请成功过"）。
    /// </summary>
    bool IsHeld { get; }

    /// <summary>
    /// 本机能不能做这件事。不支持的平台返回 <c>false</c>，<paramref name="note"/> 说明为什么。
    /// </summary>
    bool IsSupported(out string note);

    /// <summary>
    /// 申请唤醒锁（幂等）。<b>成功时也会先尝试点亮一次显示器</b>
    /// —— 只预防不点亮，等于对着已经黑掉的屏做无用功。
    /// </summary>
    /// <param name="note">成功时的说明（含"点亮有没有成功"），或失败的原因。</param>
    bool Acquire(out string note);

    /// <summary>释放唤醒锁（幂等）。恢复系统原本的息屏策略。</summary>
    /// <param name="note">成功时的说明，或失败的原因。</param>
    bool Release(out string note);
}
