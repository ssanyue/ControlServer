namespace ControlServer.Core.Licensing;

/// <summary>
/// 设备授权状态。
/// <para>
/// <b>取值与参照实现（HudongJingling）逐一对齐</b>，便于两边的文档与运维话术互相映射，
/// 也便于将来把已有客户的授权文件迁过来时，状态含义不会错位。
/// </para>
/// <para>
/// ⚠️ <b>枚举值本身是有序数字，但代码里绝不允许用范围比较来判"可用"</b> ——
/// 参照实现曾在导入流程里写 <c>ri &gt;= 1</c>，把"已过期(2)""错误(3)"一并当成成功，
/// 结果是<b>导入一份过期授权后门控关闭、直接放行且毫无提示</b>。
/// 判可用一律走 <see cref="LicenseState.IsUsable"/> 这个<b>白名单</b>。
/// </para>
/// </summary>
public enum LicenseStatus
{
    /// <summary>没有授权文件（或文件被移除）。可由管理员「生成试用」或「导入正式授权」自救。</summary>
    NotAuthorized = 0,

    /// <summary>正式授权有效。</summary>
    Authorized = 1,

    /// <summary>授权已过期。</summary>
    Expired = 2,

    /// <summary>
    /// 授权不可用：签名无效 / 机器码不符 / 缺联网锚点 / 时钟早于锚点 / 版本不支持。
    /// <para>与 <see cref="Expired"/> 分开，是因为"过期"有明确的续期路径，而这一类需要先查清原因。</para>
    /// </summary>
    Error = 3,

    /// <summary>试用授权有效（仍在试用期内）。</summary>
    Trial = 4,
}
