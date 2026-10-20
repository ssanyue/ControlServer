namespace ControlServer.Core.Abstractions;

/// <summary>
/// 授权签名校验端口。
/// <para>
/// 抽成接口的目的不是"将来可能换算法"（签名算法不该换来换去），
/// 而是让单测能够<b>用自己生成的密钥对</b>签一份授权再验 —— 否则每个授权相关的用例
/// 都得依赖内嵌的厂商私钥，而那个私钥按设计根本不存在于测试环境。
/// </para>
/// <para>实现方必须保证：<b>验签失败是返回 <c>false</c>，不是抛异常</b>（畸形签名是预期输入）。</para>
/// </summary>
public interface ILicenseSignatureVerifier
{
    /// <summary>
    /// 校验签名。
    /// </summary>
    /// <param name="payload">待验签的载荷文本（<c>LicenseCodec.BuildPayload</c> 的产物，<b>不含 sig 字段</b>）。</param>
    /// <param name="base64Signature">Base64（或 Base64URL）编码的签名。</param>
    /// <returns>签名是否有效。任何解析异常都返回 <c>false</c>。</returns>
    bool Verify(string payload, string base64Signature);

    /// <summary>验签器是否可用（公钥无法加载时为 <c>false</c>）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用原因；可用时为 <c>null</c>。</summary>
    string? UnavailableReason { get; }

    /// <summary>当前生效的公钥指纹（供诊断端点回显）。</summary>
    string PublicKeyFingerprint { get; }
}
