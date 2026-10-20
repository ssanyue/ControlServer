namespace ControlServer.Core.Licensing;

/// <summary>
/// 本机自动试用记录。
///
/// <para>
/// ⚠️ <b>这是一处必须讲清楚的方案偏差</b>。原方案（§8.2）写的是"首次启动自动生成 30 天试用授权"，
/// 照字面实现就是"服务端自己签一份 <c>lt=trial</c> 授权文件"。但服务端<b>只有公钥</b>
/// （这正是把 HMAC 换成 ECDSA 的目的），它<b>签不出任何东西</b>。
/// 要让它能签，只有两条路，都不好：
/// </para>
/// <list type="number">
/// <item><description>
/// 把"试用私钥"内嵌进 exe —— 等于在交付物里放一把私钥。虽然可以把它的权力限制在
/// "只能签 trial"，但"私钥内嵌"这件事本身会诱使后来者复用这把钥匙做别的事，
/// 而且每次解释它都要重新论证一遍为什么安全。
/// </description></item>
/// <item><description>
/// 首次启动现生成一对本地密钥存在数据目录里 —— 那么"公钥"也一起躺在攻击者手边，
/// 他换掉密钥对就能自签任意试用。密码学强度等于零，却要付出"看起来有防护"的误解成本。
/// </description></item>
/// </list>
///
/// <para>
/// <b>所以本机试用被明确定义为「本地策略状态」而不是「凭证」</b>：一份不带签名的 JSON，
/// 记录"这台机器从哪天起进入试用、试用多少天"。它不需要密码学保护 ——
/// 因为<b>能改这个文件的人，同样能直接删掉整个数据目录</b>（单机离线授权的固有上限，
/// 方案 §6.3 已如实说明）。给它套一层假签名，只是把"删目录重置"换成"改文件重置"，
/// 防护没增加，却会让人误以为它拦得住。
/// </para>
///
/// <para>
/// 与之相对，<b>厂商签发的授权仍然是完整的签名凭证</b>，走 ECDSA 验签、
/// 机器码绑定、联网锚点全套。两者在 <see cref="LicenseStatus.Trial"/> 这个状态上汇合，
/// 由前端统一展示为"试用中"。
/// </para>
/// </summary>
/// <param name="MachineCode">生成这份试用时本机的机器码（换机器不认）。</param>
/// <param name="IssuedAtUtcMs">试用起始时刻（UTC 毫秒戳）。</param>
/// <param name="ExpiresOn">试用到期日（<c>yyyyMMdd</c>，北京日期，含当日）。</param>
/// <param name="Days">试用总天数（仅用于展示与审计）。</param>
/// <param name="AnchorSource">
/// 起点时间取自哪里：<c>ntp</c> = 成功联网校时；<c>local</c> = 联网不可得，退回本机校正时钟。
/// <para>如实记录来源，是为了事后能判断"这份试用的起点可不可信"。</para>
/// </param>
public sealed record LocalTrialRecord(
    string MachineCode,
    long IssuedAtUtcMs,
    string ExpiresOn,
    int Days,
    string AnchorSource)
{
    /// <summary>时间来源：联网校时。</summary>
    public const string AnchorFromNetwork = "ntp";

    /// <summary>时间来源：本机校正时钟（联网不可得时的降级路径）。</summary>
    public const string AnchorFromLocalClock = "local";
}
