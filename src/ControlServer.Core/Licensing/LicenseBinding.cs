namespace ControlServer.Core.Licensing;

/// <summary>
/// 通用授权的「首次导入绑定」结果，存为授权文件旁边的<b>侧车文件</b>。
///
/// <para>
/// <b>为什么必须有这个侧车，而不是像参照实现那样把机器码回填进授权文件本身</b>：
/// 参照实现用的是 <b>HMAC 对称签名</b>，客户端握着密钥，改完字段可以自己重签，所以"回填 <c>mc</c>"
/// 是可行的。服务版把签名换成了 <b>ECDSA 非对称</b>（私钥只在签发工具里），
/// 服务端<b>只有公钥、无法重签</b> —— 一旦回填 <c>mc</c>，签名立刻失效，整个文件作废。
/// </para>
///
/// <para>
/// 所以绑定结果只能另存一处：授权文件保持<b>签发时的原样</b>（签名完好），
/// 绑定事实记在 <c>license.binding.json</c>。评估器判定"绑定机器码"时取的顺序是
/// <c>文档 mc 非空 ? 文档 mc : 侧车 mc</c>。
/// </para>
///
/// <para>
/// ⚠️ <b>必须如实说明的弱点</b>：侧车没有签名（服务端签不了）。能改这个文件的人，
/// 就能把绑定改到另一台机器上。但请注意这并不降低整体防护水平 ——
/// 能改文件的人同样可以直接删掉整个数据目录重装（单机离线授权的固有上限，
/// 见方案 §6.3）。侧车的作用是让绑定语义<b>成立且可追溯</b>，
/// 而不是提供密码学强度。要真正防住，需要服务端/云端激活。
/// </para>
/// </summary>
/// <param name="MachineCode">绑定的机器码。</param>
/// <param name="BoundAtUtcMs">绑定时刻（UTC 毫秒戳）。</param>
public sealed record LicenseBinding(string MachineCode, long BoundAtUtcMs);
