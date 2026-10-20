namespace ControlServer.Core.Services;

/// <summary>
/// 生产环境的 <see cref="TimeProvider"/>：在系统时钟之上叠加 NTP 校正量
/// （<see cref="BeijingTime.ClockOffset"/>），让所有依赖 <see cref="TimeProvider"/>
/// 的组件自动跟随校正，无需各自去读静态类。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：定时调度需要 <see cref="TimeProvider"/> 才能在测试里注入可推进的假时钟
/// （定时逻辑不可能靠真实等待来测）。但生产环境的时钟又不是纯系统钟 ——
/// 它必须是"系统钟 + 校正量"。这个类型就是把两者接起来的那一层。
/// </para>
/// <para>
/// <b>与 <see cref="BeijingTime"/> 的分工</b>：本类型只负责"UTC 视角的当前时刻（含校正）"。
/// 至于偏移量是 +08:00 还是别的，由调用方用 <see cref="BeijingTime.ToBeijing"/> 决定 ——
/// 本类型刻意不把 +08:00 写死，否则测试注入的假时钟就会被无声地再偏移一次。
/// </para>
/// <para>
/// <b>单例</b>：无状态（每次都读静态的 <see cref="BeijingTime.ClockOffset"/>），
/// 因此注册为单例即可，不需要每次解析。
/// </para>
/// </remarks>
public sealed class CorrectedTimeProvider : TimeProvider
{
    /// <summary>进程内共享实例。</summary>
    public static readonly CorrectedTimeProvider Instance = new();

    /// <summary>校正后的 UTC 时刻。</summary>
    public override DateTimeOffset GetUtcNow()
        => DateTimeOffset.UtcNow.Add(BeijingTime.ClockOffset);
}
