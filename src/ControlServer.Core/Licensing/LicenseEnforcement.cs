namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权闸门的执行档位。
/// </summary>
public enum LicenseEnforcement
{
    /// <summary>完全不校验，仅记录并展示授权状态。内部开发 / 明确不需要授权的部署用。</summary>
    Off,

    /// <summary>
    /// 校验并展示，但<b>不拦截</b>任何功能（后台监听与定时照常运行）。
    /// <para>默认档位。它是为了让"升级"这件事本身永远不锁死既有部署。</para>
    /// </summary>
    Warn,

    /// <summary>按闸门矩阵拦截：核心价值点（执行动作、后台监听、定时触发）在未授权时不可用。</summary>
    Enforce,
}
