using System.IO;
using System.Reflection;
using ControlServer.Core.Licensing;

namespace ControlServer.LicenseTool;

/// <summary>
/// 签发工具<b>内嵌</b>的厂商私钥（GUI 与命令行共用这一份）。
///
/// <para>
/// <b>它解决的问题</b>：此前的流程要求签发人自己去保管、指认一个 <c>vendor-private.pem</c> 文件。
/// 这带来三件事——文件会丢、会拿错、会被"生成密钥对"页悄悄替换掉，而每一次的后果都是
/// "签出来的授权在客户那里验不过"。把私钥直接编进工具之后，
/// 签发人<b>只面对一个 exe</b>：双击、填表、点「签发」。密钥文件这一层从操作流程里消失了。
/// </para>
///
/// <para>
/// <b>密钥来自哪里</b>：<c>.tools/license-keys/vendor-private.pem</c>（已 gitignore），
/// 两个签发工具的 csproj 在构建时把它作为 <see cref="EmbeddedResourceAttribute"/> 嵌进程序集。
/// 因此<b>本文件不含任何秘密</b>，可以放心入库；秘密只存在于被内嵌的那份 .pem 与最终的 exe 里。
/// </para>
///
/// <para>
/// <b>为什么不能放进 <c>ControlServer.Core</c></b>：Core 会被 <c>ControlServer.Server</c> 引用。
/// 私钥若放进 Core，就会被<b>编译进交付给客户的每一份服务端</b> —— 那等于把签发权公开发放。
/// 所以它只存在于 <c>tools/</c> 下的签发工具项目里，服务端只有配对的公钥（<see cref="VendorPublicKey"/>）。
/// </para>
///
/// <para>
/// <b>安全边界（必须知情）</b>：私钥内嵌意味着<b>拿到这个 exe 的人就能签任意授权</b>
/// （单文件 .NET 程序里的字符串是可提取的）。因此这两个签发工具必须始终当作
/// <b>厂商内部机密</b>：不随服务端交付、不进共享目录、不发给客户。
/// 这与"厂商内部工具"的既有定位一致，只是现在"工具本身就是钥匙"。
/// </para>
/// </summary>
internal static class LicenseToolKey
{
    /// <summary>内嵌资源名。<b>两个签发工具的 csproj 用同一个 LogicalName</b>，这里才敢按固定名取。</summary>
    private const string ResourceName = "vendor-private.pem";

    /// <summary>懒加载：读一次、算一次指纹，之后复用（私钥解析不便宜，且界面会频繁取值）。</summary>
    private static readonly Lazy<ProbeResult> Cached = new(Probe, isThreadSafe: true);

    /// <summary>工具是否真的内嵌了私钥（正常情况下恒为 <c>true</c>）。</summary>
    public static bool IsAvailable => Cached.Value.Pem is not null;

    /// <summary>内嵌私钥的 PEM 内容。未内嵌时抛出，消息里带"怎么办"。</summary>
    /// <exception cref="InvalidOperationException">程序集里没有内嵌私钥。</exception>
    public static string Pem => Cached.Value.Pem
        ?? throw new InvalidOperationException(UnavailableMessage(Cached.Value.Error));

    /// <summary>该私钥对应公钥的指纹（与服务端 <see cref="VendorPublicKey.Fingerprint"/> 可直接比对）。</summary>
    public static string Fingerprint => Cached.Value.Fingerprint;

    /// <summary>与服务端内嵌公钥是否配对 —— 一致才意味着"签出来的授权服务端会收"。</summary>
    public static bool MatchesServer => string.Equals(
        Fingerprint, VendorPublicKey.Fingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>未内嵌时的原因（供界面/命令行如实说明）。可用时为 <c>null</c>。</summary>
    public static string? Error => Cached.Value.Error;

    /// <summary>给使用者看的、一句话能照做的不可用说明。</summary>
    public static string UnavailableMessage(string? reason = null)
        => "本签发工具没有内嵌厂商私钥，无法签发。"
           + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"\n原因：{reason}")
           + "\n这是构建环节的问题：请确认 .tools/license-keys/vendor-private.pem 存在后重新构建签发工具，"
           + "\n或用 LicenseTool new-key 生成新密钥对并同步更新服务端内嵌公钥。";

    private static ProbeResult Probe()
    {
        try
        {
            using var stream = typeof(LicenseToolKey).Assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return new ProbeResult(null, string.Empty, "程序集里找不到名为 vendor-private.pem 的内嵌资源。");
            }

            using var reader = new StreamReader(stream);
            var pem = reader.ReadToEnd();

            if (string.IsNullOrWhiteSpace(pem))
            {
                return new ProbeResult(null, string.Empty, "内嵌的 vendor-private.pem 是空的。");
            }

            // 指纹由私钥现推 —— 与"服务端验签用的公钥从哪来"是同一个来源，因此不会对不上。
            var (_, fingerprint) = LicenseSigner.DescribeKeyPair(pem);

            return new ProbeResult(pem, fingerprint, null);
        }
        catch (Exception ex)
        {
            // 构建/资源层面的问题不该让工具崩溃：如实记下原因，交给调用方决定怎么呈现。
            return new ProbeResult(null, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private readonly record struct ProbeResult(string? Pem, string Fingerprint, string? Error);
}
