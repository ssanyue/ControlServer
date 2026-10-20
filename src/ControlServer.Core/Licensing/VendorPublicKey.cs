namespace ControlServer.Core.Licensing;

/// <summary>
/// 厂商公钥（编译期<b>内嵌</b>，不是配置项）。
///
/// <para>
/// <b>为什么必须是常量而不是 <c>Server:License:PublicKey</c> 配置项</b>：
/// 授权体系的全部安全性都建立在"只有厂商能签"这一条上。若公钥可配置，
/// 任何一个能改配置文件的人（现场运维、甚至只是能写 <c>appsettings.json</c> 的人）
/// 都可以把自己的公钥填进去，再用自己的私钥签一份"永久、不绑机器"的授权 ——
/// 整套授权<b>当场归零</b>，而且服务端日志里看不出任何异常。
/// </para>
///
/// <para>
/// <b>操作者看不到"钥匙"这一层</b>：私钥已<b>编译进签发工具</b>
/// （<c>ControlServer.LicenseTool.Gui.exe</c> / <c>ControlServer.LicenseTool.exe</c>，
/// 见 <c>tools/LicenseToolKey.cs</c>）。签发人双击工具、填表、点「签发」即可，
/// 不需要、也不应该去管任何 <c>.pem</c> 文件。本文件里的公钥对他们同样不可见 ——
/// 它只是服务端二进制里的一个常量，随服务端一起交付。
/// </para>
///
/// <para>
/// <b>为什么公钥"不能取消"</b>：验签必须有公钥。若真的去掉它，服务端就只能"无条件相信"
/// 送进来的授权文件，任何人手改一个字段（或直接编一份）都会生效，授权体系等于没有。
/// 所以"简化"能简化掉的是<b>人的操作</b>（密钥文件、选钥、比指纹），
/// 而不是<b>密码学本身</b> —— 后者只需要保留在这一处常量里。
/// </para>
///
/// <para>
/// <b>代价（必须知情）</b>：换密钥要重编服务端并重新交付。轮换步骤见
/// <c>docs/授权操作手册.md</c>：① <c>LicenseTool new-key --force</c> 生成新密钥对；
/// ② 把新公钥替换到本文件；③ 重编交付服务端；④ 用新私钥重新签发客户授权。
/// （若要支持"新旧公钥并存过渡"，把 <see cref="Pem"/> 改成字符串数组即可，
/// 但那样会让"当前生效的到底是哪把钥匙"变得含糊，暂不做。）
/// </para>
///
/// <para>
/// <b>当前内嵌的是正式厂商密钥</b>（2026-10-10 由 <c>LicenseTool new-key</c> 生成并替换）。
/// 其私钥位于 <c>.tools/license-keys/vendor-private.pem</c>（已加 .gitignore，
/// <b>绝不可随交付物分发</b>），构建时被内嵌进<b>签发工具</b>；
/// 一旦泄漏，任何人可签发"任意机器、永久有效"的授权。
/// </para>
/// </summary>
public static class VendorPublicKey
{
    /// <summary>
    /// 公钥指纹（公钥 DER 的 SHA-256 前 8 字节十六进制，共 16 个字符）。
    /// <para>用于快速确认"服务端内嵌的是哪把钥匙" —— 换密钥后本值必须同步更新，否则诊断信息会说谎。</para>
    /// </summary>
    public const string Fingerprint = "D14EE793C89328E7";

    /// <summary>SubjectPublicKeyInfo 格式的 PEM 公钥。</summary>
    public const string Pem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsOxH73HSNBx1y75Zzt8qqa6S3F82
u1zduK1FI5WeI0opnW0i9YHni5AzMzjmRDD0JgVrng8ds9iX8F/YpFZ9QQ==
-----END PUBLIC KEY-----
""";
}
