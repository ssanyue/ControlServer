using System.Globalization;
using ControlServer.Core.Licensing;

namespace ControlServer.LicenseTool.Commands;

/// <summary>
/// <c>inspect</c> —— 解读一份 .lic。
///
/// <para>
/// <b>为什么需要它</b>：授权流程里的故障几乎都不是"程序错了"，而是
/// "这份文件到底是不是我以为的那份"。客户把 .lic 发过来、同事把文件转错、
/// 换过密钥后没重编服务端…… 这些情形下最先要回答的问题都是同一个：
/// <b>它绑的是哪台机器、什么时候到期、签名对不对</b>。
/// 没有这个命令，就只能靠"导进去试试看"，而那是靠猜。
/// </para>
///
/// <para>
/// 只需要公钥，不需要私钥；也不需要知道本机机器码 —— 因此厂商、销售、支持人员都能用。
/// </para>
/// </summary>
internal static class InspectCommand
{
    public const string Name = "inspect";

    public const string Help = """
        用法：
          ControlServer.LicenseTool inspect <授权文件.lic> [--key <公钥文件>]

        参数：
          <授权文件.lic>   要解读的文件（必填，唯一位置参数）
          --key            用于验签的公钥（默认 = 服务端内嵌的厂商公钥）
                           传私钥文件也可以：ECDSA 能从私钥里取出公钥

        说明：
          打印授权文件的全部字段、签名校验结果，以及"这份授权在它自己的绑定机器上
          此刻会是什么状态"（有效期、是否已过期、缺少联网锚点等）。

          注意：本命令不掌握本机的机器码，因此它回答的不是"在这台机器上能不能用"，
          而是"在它绑定的那台机器上能不能用"。对绑定式授权来说后者才是你关心的。

        退出码：0 签名有效且可用；2 文件不可用（解析失败 / 签名无效 / 已过期等）；1 命令用法有误。
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = Arguments.Parse(args);

        if (parsed.Has("help") || parsed.Has("h"))
        {
            ConsoleOut.Line(Help);
            return 0;
        }

        parsed.RejectUnknown(Name, "key", "help", "h");

        if (parsed.Positional.Count == 0)
        {
            throw new UsageException(Name, "缺少授权文件路径。");
        }

        if (parsed.Positional.Count > 1)
        {
            throw new UsageException(Name, $"多余的位置参数「{parsed.Positional[1]}」（本命令只接受一个文件）。");
        }

        var path = Path.GetFullPath(parsed.Positional[0]);
        var text = await FileIo.ReadRequiredAsync(path, "授权文件").ConfigureAwait(false);

        // 默认用服务端内嵌的厂商公钥 —— 这正是"客户发来的文件在服务端能不能过"这个问题的答案。
        var customKeyPath = parsed.Value("key");
        var publicKeyPem = customKeyPath is null
            ? VendorPublicKey.Pem
            : await FileIo.ReadRequiredAsync(Path.GetFullPath(customKeyPath), "公钥文件").ConfigureAwait(false);

        var inspection = LicenseInspector.Inspect(
            text, publicKeyPem, DateTimeOffset.UtcNow, LicenseEvaluator.DefaultClockTolerance);

        Print(path, inspection);

        return inspection.SignatureValid && inspection.State.IsUsable ? 0 : 2;
    }

    /// <summary>
    /// 本报告里最长的标签是「在绑定机器上的状态」（18 列），
    /// 因此标签列宽取 18；沿用默认的 12 会让那一行把值挤到下一行的视觉位置。
    /// </summary>
    private const int LabelWidth = 18;

    private static void Field(string label, string value) => ConsoleOut.Field(label, value, LabelWidth);

    private static void Print(string path, LicenseInspection inspection)
    {        var document = inspection.Document;
        var file = new FileInfo(path);

        ConsoleOut.Section("授权文件");
        Field("路径", path);
        Field("大小", $"{file.Length} 字节");
        Field("文件版本", document.V.ToString(CultureInfo.InvariantCulture));

        ConsoleOut.Section("内容");
        Field("授权类型", $"{LicenseKind.DisplayName(document.Lt)}（{document.Lt}）");
        Field(
            "绑定机器码",
            document.Ub == 1 && string.IsNullOrWhiteSpace(document.Mc)
                ? "通用授权（尚未绑定，由客户首次导入时绑定）"
                : ConsoleOut.OrNone(document.Mc));
        Field("签发日", FormatLicenseDate(document.Id));
        Field("有效期", DescribeExpiry(document));
        Field("序列号", ConsoleOut.OrNone(document.Sn));
        Field("厂商标识", ConsoleOut.OrNone(document.Vi));
        Field("客户备注", ConsoleOut.OrNone(document.Hn));
        Field("联网锚点", DescribeUtcMs(document.Ivt, "ivt"));
        Field("绑定时刻", DescribeUtcMs(document.Bt, "bt"));
        Field("使用截止", DescribeUtcMs(document.Ud, "ud"));

        ConsoleOut.Section("校验");
        Field("签名", inspection.SignatureValid ? "通过" : "不通过");

        if (!inspection.SignatureValid)
        {
            Field("原因", inspection.SignatureError);
        }

        Field(
            "验签公钥",
            $"{inspection.PublicKeyFingerprint}"
            + (inspection.SignedByVendorKey ? "（服务端内嵌的厂商公钥）" : "（不是服务端内嵌的厂商公钥）"));

        // 签名不通过时，评估器的 Info 就是那句签名错误 —— 已经在上面「原因」里印过一次了，
        // 这里再拼一遍会让报告出现一大段重复文字，看起来像出了两个不同的毛病。
        Field(
            "在绑定机器上的状态",
            inspection.SignatureValid
                ? $"{inspection.State.StatusDisplayName}：{inspection.State.Info}"
                : $"{inspection.State.StatusDisplayName}（签名不通过，无法进一步判定有效期与绑定）");

        if (inspection.ExpiresAtUtc is not null && inspection.State.RemainingDays >= 0)
        {
            Field(
                "剩余天数",
                inspection.State.IsPerpetual
                    ? "永久"
                    : inspection.State.RemainingDays.ToString(CultureInfo.InvariantCulture) + " 天");
        }

        // ── 提示 ──
        ConsoleOut.Section("结论");

        var healthy = inspection.SignatureValid && inspection.State.IsUsable;

        if (healthy)
        {
            ConsoleOut.Ok("可用：签名通过，且当前在有效期内。");
        }
        else
        {
            ConsoleOut.Fail("不可用：这份文件不会被服务端接受（原因见上）。");
        }

        if (!inspection.SignedByVendorKey)
        {
            ConsoleOut.Hint($"本次验签用的不是服务端内嵌的厂商公钥（内嵌指纹 {VendorPublicKey.Fingerprint}）。");
            ConsoleOut.Hint("若你刚换过密钥但还没重编服务端，这属于预期情况；用 --key 指向新公钥即可确认新授权。");
        }

        if (document.Ivt <= 0)
        {
            ConsoleOut.Hint("缺少 ivt 联网校对锚点 —— 这是手工拼装授权文件的典型特征，服务端一律拒绝。");
        }

        if (inspection.SignatureValid && !inspection.State.IsUsable)
        {
            ConsoleOut.Hint("签名是好的但当前不可用，通常是：已过期、机器码不符、或缺少联网锚点。");
        }

        ConsoleOut.Line();
        ConsoleOut.Hint("提醒：本命令不掌握本机机器码，回答的是「在它绑定的机器上」的状态。");
    }

    private static string DescribeExpiry(LicenseDocument document)
    {
        if (document.Et > 0)
        {
            return BeijingInput.Describe(DateTimeOffset.FromUnixTimeMilliseconds(document.Et)) + "（et）";
        }

        if (!string.IsNullOrEmpty(document.Ed))
        {
            return document.Ed.Length == 8
                ? $"{FormatLicenseDate(document.Ed)} 当天 23:59:59 结束（ed）"
                : document.Ed;
        }

        if (document.Md > 0)
        {
            return $"自绑定时刻起 {document.Md} 天（md）";
        }

        return "永久（不按日期限制）";
    }

    private static string FormatLicenseDate(string? yyyyMMdd)
    {
        if (string.IsNullOrWhiteSpace(yyyyMMdd) || yyyyMMdd.Length != 8
            || !LicenseEvaluator.TryParseBeijingDate(yyyyMMdd, out var date))
        {
            return ConsoleOut.OrNone(yyyyMMdd);
        }

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>把 UTC 毫秒戳字段渲染成可读文本，<c>0</c> 显示成「（未设置）」而不是一个裸 0。</summary>
    private static string DescribeUtcMs(long value, string fieldName)
    {
        if (value <= 0)
        {
            // ivt 缺失是**异常**（服务端会拒绝），必须显眼；bt/ud 为 0 是正常状态。
            return fieldName == "ivt" ? "（未设置 —— 服务端会拒绝）" : $"（未设置）（{fieldName}）";
        }

        return $"{BeijingInput.Describe(DateTimeOffset.FromUnixTimeMilliseconds(value))}（{fieldName}）";
    }
}
