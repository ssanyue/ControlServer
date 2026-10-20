using ControlServer.Core.Licensing;

namespace ControlServer.Core.Tests.Licensing;

/// <summary>
/// 授权测试的共享夹具。
///
/// <para>
/// <b>为什么每个测试类都现场生成密钥对，而不是复用内嵌的厂商公钥私钥</b>：
/// 厂商私钥按设计<b>根本不存在于测试环境</b>（它只应躺在签发人机器的 <c>.tools/</c> 下）。
/// 测试要签的东西，就该由测试自己造钥匙来签 —— 顺带还验证了"验签器接受任意外部公钥"这件事，
/// 而不是只对某一把写死的钥匙有效。
/// </para>
/// </summary>
internal static class LicenseTestKit
{
    /// <summary>测试用机器码。</summary>
    public const string MachineCode = "AAAA-BBBB-CCCC-DDDD";

    /// <summary>另一台机器的机器码（用于"绑定不符"用例）。</summary>
    public const string OtherMachineCode = "1111-2222-3333-4444";

    /// <summary>判定基准时刻：2026-10-08 12:00:00 UTC。</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static (string PrivateKeyPem, string PublicKeyPem) NewKeyPair()
    {
        var (privatePem, publicPem, _) = LicenseSigner.GenerateKeyPair();
        return (privatePem, publicPem);
    }

    /// <summary>构造一份字段齐全、签名前状态合法的授权文档（未签名）。</summary>
    public static LicenseDocument NewDocument(
        string kind = LicenseKind.Full,
        string? machineCode = MachineCode,
        string? expiresOn = "20271008",
        long exactExpiryUtcMs = 0,
        int maxDays = 0,
        int universal = 0,
        long importDeadlineUtcMs = 0,
        long issuedAtUtcMs = 0,
        long networkVerifiedUtcMs = 0)
        => new()
        {
            V = LicenseDocument.CurrentVersion,
            Mc = machineCode ?? string.Empty,
            Lt = kind,
            Id = "20261008",
            Ed = expiresOn ?? string.Empty,
            Et = exactExpiryUtcMs,
            Md = maxDays,
            Sn = "SN-0001",
            Vi = "VENDOR-0001",
            Hn = "测试展厅",
            Ub = universal,
            Ud = importDeadlineUtcMs,
            Bt = issuedAtUtcMs,
            // 缺省锚点＝签发时刻（本月之前一点点），保证"锚点早于现在"这一前提成立。
            Ivt = networkVerifiedUtcMs > 0 ? networkVerifiedUtcMs : Now.AddDays(-1).ToUnixTimeMilliseconds(),
        };

    /// <summary>签发（含签名）并返回可分发文本。</summary>
    public static string Issue(LicenseDocument document, string privateKeyPem)
        => LicenseSigner.Sign(document, privateKeyPem);

    /// <summary>按服务端那条路径验签（解码 → 重算载荷 → 验签）。</summary>
    public static bool Verify(string encodedLicense, string publicKeyPem)
        => LicenseSigner.VerifyRoundTrip(encodedLicense, publicKeyPem);

    /// <summary>用给定文档直接构造评估输入（默认"签名有效、机器码相符"）。</summary>
    public static LicenseEvaluationInput Input(
        LicenseDocument? document,
        bool signatureValid = true,
        string machineCode = MachineCode,
        DateTimeOffset? nowUtc = null,
        LicenseBinding? binding = null,
        string? signatureError = null)
        => new(
            document,
            binding,
            signatureValid,
            signatureError,
            machineCode,
            nowUtc ?? Now,
            LicenseEvaluator.DefaultClockTolerance);

    /// <summary>把北京日期（<c>yyyyMMdd</c>）转成当天 23:59:59 的 UTC 毫秒戳。</summary>
    public static long BeijingDayEndUtcMs(int year, int month, int day)
        => new DateTimeOffset(year, month, day, 23, 59, 59, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
}
