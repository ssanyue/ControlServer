using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Core.Licensing;

namespace ControlServer.LicenseTool.Commands;

/// <summary>
/// <c>gen</c> —— 签发一份授权。
///
/// <para>
/// 本命令只负责"把命令行翻译成 <see cref="LicenseIssuanceRequest"/> + 落盘 + 报告"，
/// 全部规则（永久必须绑机器码、通用必须有使用截止、有效期三选一…）都在
/// <see cref="LicenseIssuer"/> 里 —— 那里同时被单测覆盖，两处各写一遍必然会漂移。
/// </para>
///
/// <para>
/// <b>参数校验刻意排在联网取时之前</b>：NTP 取时最坏要等十几秒，若把参数解析放在其后，
/// 一个 <c>--type ful</c> 的错字要等到网络超时之后才被告知；更糟的是"网络不通 + 参数也写错"时，
/// 用户会先去折腾网络，修好再重跑，然后才发现真正的错在参数上。
/// </para>
/// </summary>
internal static class GenerateCommand
{
    public const string Name = "gen";

    public const string Help = """
        用法：
          ControlServer.LicenseTool gen --type full|trial|temp
                                         (--bind <机器码> | --universal)
                                         [--expire <北京时间> | --days <天数>]
                                         [--deadline <北京时间>]
                                         [--sn <序列号>] [--vendor <厂商标识>] [--customer <客户备注>]
                                         [--key <私钥文件>] [--anchor <北京时间>] [--offline]
                                         [--out <输出文件>] [--force]

        授权对象（二选一，必填）：
          --bind <机器码>   绑定到指定机器，形如 AAAA-BBBB-CCCC-DDDD
                            （客户在服务端「授权」页复制后发给你）
          --universal       通用授权：签发时不绑定，由客户首次导入时自动绑定
                            （必须同时给 --deadline）

        有效期（三选一，全部留空 = 永久）：
          --expire "2027-10-08 23:59:59"   精确到期时刻（北京时间）
          --expire 2027-10-08              到期日，当天 23:59:59 结束
          --days 30                        自签发起 30 天
          注意：永久授权必须绑定机器码，不允许 "--universal 且无期限"。

        其它：
          --deadline <北京时间>  仅通用授权：最迟可导入绑定的时间（必填）
          --sn / --vendor / --customer   仅展示用途，会写进授权文件
          --key <私钥文件>       高级：改用指定的私钥（默认用工具内嵌的厂商密钥）
          --anchor <北京时间>    显式指定 ivt 联网锚点（默认自动取网络时间）
          --offline              取不到网络时间时，接受用本机时钟当锚点（不推荐）
          --out <文件>           输出路径（默认按客户/序列号生成）
          --force                允许覆盖已存在的输出文件

        退出码：0 成功；1 命令用法有误；2 规则不允许（如永久+通用）；3 未预期错误。
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = Arguments.Parse(args);

        if (parsed.Has("help") || parsed.Has("h"))
        {
            ConsoleOut.Line(Help);
            return 0;
        }

        parsed.RejectUnknown(
            Name,
            "type", "bind", "universal", "expire", "days", "deadline",
            "sn", "vendor", "customer", "key", "anchor", "offline", "out", "force", "help", "h");

        if (parsed.Positional.Count > 0)
        {
            throw new UsageException(Name, $"多余的位置参数「{parsed.Positional[0]}」。");
        }

        // ① 先把参数全部解析并校验完（这一步不碰网络、不读盘）。
        var options = ParseOptions(parsed);

        // ② 取私钥：默认用工具内嵌的厂商密钥 —— 绝大多数签发都不该、也不需要关心密钥文件在哪。
        var privateKeyPem = await ResolvePrivateKeyAsync(parsed).ConfigureAwait(false);

        // ③ 定签发时刻与联网锚点。
        var (issuedAt, anchor) = await ResolveIssuedAtAndAnchorAsync(parsed, DateTimeOffset.UtcNow)
            .ConfigureAwait(false);

        var request = BuildRequest(options, issuedAt, anchor);

        ConsoleOut.Line();
        ConsoleOut.Line("正在签发（含密码学自检与服务端导入规则干跑）…");

        var result = LicenseIssuer.Issue(request, privateKeyPem);

        var outputPath = Path.GetFullPath(parsed.Value("out") ?? DefaultFileName(options));

        if (File.Exists(outputPath) && !parsed.Has("force"))
        {
            ConsoleOut.Error($"输出文件已存在，已停止：{outputPath}");
            ConsoleOut.Hint("加 --force 覆盖，或用 --out 换一个文件名。");
            throw new ToolAbortException();
        }

        FileIo.WriteTextNoBom(outputPath, result.Encoded);

        PrintSummary(outputPath, request, result);

        return 0;
    }

    /// <summary>
    /// 取本次签发要用的私钥。
    ///
    /// <para>
    /// 默认用工具<b>内嵌</b>的厂商密钥（见 <c>tools/LicenseToolKey.cs</c>）——
    /// 于是"签发"就是单纯的填参数、出文件，不牵扯任何密钥文件。
    /// 只有显式给 <c>--key</c> 时才去读外部文件，留给换钥、自检等高级场景。
    /// </para>
    /// </summary>
    private static async Task<string> ResolvePrivateKeyAsync(Arguments parsed)
    {
        if (parsed.Value("key") is { } keyArgument)
        {
            var keyPath = Path.GetFullPath(keyArgument);

            if (!File.Exists(keyPath))
            {
                ConsoleOut.Error($"找不到 --key 指定的私钥文件：{keyPath}");
                ConsoleOut.Hint("不指定 --key 时会用工具内嵌的厂商密钥；要换钥请先看 new-key --help。");
                throw new ToolAbortException();
            }

            ConsoleOut.Hint($"使用 --key 指定的私钥文件：{keyPath}");
            return await FileIo.ReadTextAsync(keyPath).ConfigureAwait(false);
        }

        // 内嵌密钥缺失是构建环节的问题，不是使用者的操作失误 —— 说清这一点，别让人去翻文件。
        if (!LicenseToolKey.IsAvailable)
        {
            ConsoleOut.Error(LicenseToolKey.UnavailableMessage(LicenseToolKey.Error));
            throw new ToolAbortException();
        }

        ConsoleOut.Hint($"签发密钥：内置厂商密钥（公钥指纹 {LicenseToolKey.Fingerprint}）");
        return LicenseToolKey.Pem;
    }

    // ═══════════════════════════════════════════════════════════
    // 参数 → 中间形态
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 解析并校验后的命令行参数。
    /// <para>
    /// 存在的意义是把"读参数"与"确定签发时刻"分开：后者要先联网，
    /// 而前者不该等网络。中间放一个不含"现在几点"的形态，两边就都干净了。
    /// </para>
    /// </summary>
    private sealed record GenerateOptions(
        string Kind,
        bool Universal,
        string MachineCode,
        long ExactExpiryUtcMs,
        string ExpiryDate,
        int MaxDays,
        DateTimeOffset? Deadline,
        string Serial,
        string Vendor,
        string Customer);

    private static GenerateOptions ParseOptions(Arguments parsed)
    {
        // ── 类型 ──
        var kind = parsed.ValueOr("type", LicenseKind.Full);

        if (!LicenseKind.IsValid(kind))
        {
            throw new UsageException(
                Name, $"授权类型「{kind}」无法识别，应为 {string.Join(" / ", LicenseKind.All)} 之一。");
        }

        // ── 授权对象 ──
        parsed.RejectTogether(Name, "bind", "universal");

        var universal = parsed.Has("universal");
        var machineCode = parsed.Value("bind") ?? string.Empty;

        if (!universal && machineCode.Length == 0)
        {
            throw new UsageException(
                Name,
                parsed.Has("bind")
                    ? "--bind 后面需要一个机器码（形如 AAAA-BBBB-CCCC-DDDD）。"
                    : "必须指定 --bind <机器码> 或 --universal 其中之一。");
        }

        // ── 有效期 ──
        parsed.RejectTogether(Name, "expire", "days");

        long exactExpiryUtcMs = 0;
        var expiryDate = string.Empty;

        if (parsed.Has("expire"))
        {
            var text = parsed.Value("expire")
                ?? throw new UsageException(Name, "--expire 后面需要一个时间或日期。");

            if (BeijingInput.TryParseInstant(text, out var instant))
            {
                exactExpiryUtcMs = instant.ToUnixTimeMilliseconds();
            }
            else if (BeijingInput.TryParseDate(text, out var date))
            {
                expiryDate = BeijingInput.ToLicenseDate(date);
            }
            else
            {
                throw new UsageException(
                    Name,
                    $"--expire 无法解析：「{text}」。支持 2027-10-08（当天结束）或 \"2027-10-08 23:59:59\"（北京时间）。");
            }
        }

        var maxDays = 0;

        if (parsed.Has("days"))
        {
            maxDays = parsed.OptionalInt(Name, "days") ?? 0;

            // ⚠️ --days 0（或负数）若不拦下，会被下游当成"没有指定有效期" ——
            // 而"没有指定有效期"的绑定式正式授权 = **永久**。
            // 一个 0 换来一份永不过期的授权，属于最不能静默发生的错误，必须在最靠近输入的地方拦。
            if (maxDays <= 0)
            {
                throw new UsageException(
                    Name, $"--days 需要正整数，但收到 {maxDays}。完全不写该参数表示永久（仅绑定式授权允许）。");
            }
        }

        // ── 使用截止（仅通用）──
        DateTimeOffset? deadline = null;

        if (parsed.Has("deadline"))
        {
            if (!universal)
            {
                throw new UsageException(
                    Name, "--deadline 只对通用授权有意义（它约束的是「首次导入绑定」这个动作）。");
            }

            var text = parsed.Value("deadline")
                ?? throw new UsageException(Name, "--deadline 后面需要一个时间。");

            if (!BeijingInput.TryParseInstant(text, out var value))
            {
                throw new UsageException(
                    Name, $"--deadline 无法解析：「{text}」。需要 \"2026-11-08 23:59:59\"（北京时间）。");
            }

            deadline = value;
        }

        return new GenerateOptions(
            kind,
            universal,
            machineCode,
            exactExpiryUtcMs,
            expiryDate,
            maxDays,
            deadline,
            parsed.ValueOr("sn", string.Empty),
            parsed.ValueOr("vendor", string.Empty),
            parsed.ValueOr("customer", string.Empty));
    }

    private static LicenseIssuanceRequest BuildRequest(
        GenerateOptions options,
        DateTimeOffset issuedAt,
        DateTimeOffset anchor) => new()
    {
        Kind = options.Kind,
        Universal = options.Universal,
        MachineCode = options.MachineCode,
        IssuedAtUtc = issuedAt,
        AnchorUtc = anchor,
        ExactExpiryUtcMs = options.ExactExpiryUtcMs,
        ExpiryDate = options.ExpiryDate,
        MaxDays = options.MaxDays,
        UseDeadlineUtc = options.Deadline,
        Serial = options.Serial,
        Vendor = options.Vendor,
        Customer = options.Customer,
    };

    // ═══════════════════════════════════════════════════════════
    // 签发时刻与联网锚点
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 确定签发时刻与 <c>ivt</c> 锚点。
    /// <para>
    /// 两者可能被统一成同一个值：如果本机时钟慢于网络时间超过容差，就用<b>网络时间</b>作为签发时刻。
    /// 理由：服务端读状态时用的是"含校正的北京时间"，若这里坚持用本机慢钟当签发时刻，
    /// 锚点就会晚于签发时刻，刚签出来的文件会被自己的规则判成"时间被回拨"。
    /// </para>
    /// </summary>
    private static async Task<(DateTimeOffset IssuedAt, DateTimeOffset Anchor)> ResolveIssuedAtAndAnchorAsync(
        Arguments parsed,
        DateTimeOffset localNow)
    {
        DateTimeOffset anchor;

        if (parsed.Value("anchor") is { } text)
        {
            if (!BeijingInput.TryParseInstant(text, out var value))
            {
                throw new UsageException(
                    Name, $"--anchor 无法解析：「{text}」。需要 \"2026-10-08 20:00:00\"（北京时间）。");
            }

            ConsoleOut.Warn($"使用 --anchor 指定的联网锚点：{BeijingInput.Describe(value)}");
            ConsoleOut.Hint("指定值不会被验证是否真的来自网络；请确认它取自可信时钟。");
            anchor = value;
        }
        else
        {
            ConsoleOut.Line("正在获取网络时间（作为 ivt 联网校对锚点）…");

            var network = await NetworkClock.TryQueryAsync(NetworkClock.DefaultTimeout).ConfigureAwait(false);

            if (network is not null)
            {
                anchor = network.Value;
            }
            else if (parsed.Has("offline"))
            {
                anchor = localNow;

                ConsoleOut.Warn($"未取得网络时间，已按 --offline 使用本机时钟：{BeijingInput.Describe(anchor)}");
                ConsoleOut.Hint("这样签出来的 ivt 来自本机钟 —— 本机时间若被改过，这个锚点就不可信了。");
            }
            else
            {
                ConsoleOut.Error("未能取得网络时间，无法写入可信的 ivt 联网锚点。");
                ConsoleOut.Hint("请检查网络与防火墙（NTP 走 UDP 123）后重试；");
                ConsoleOut.Hint("确实无法联网时，可用 --anchor \"<北京时间>\" 指定可信时刻，或用 --offline 接受本机时钟。");
                throw new ToolAbortException();
            }
        }

        // 规则本身住在 Core（LicenseIssuer.ResolveIssuedAt），GUI 走的是同一条 —— 两边不可能漂移。
        var issuedAt = LicenseIssuer.ResolveIssuedAt(localNow, anchor, LicenseEvaluator.DefaultClockTolerance);

        if (issuedAt != localNow)
        {
            ConsoleOut.Warn($"本机时钟比网络时间慢约 {(anchor - localNow).TotalSeconds:F0} 秒，已改用网络时间作为签发时刻。");
            ConsoleOut.Hint("否则刚签出的授权会因「本机时间早于联网锚点」被拒绝。建议顺手校准系统时间。");
        }

        return (issuedAt, anchor);
    }

    // ═══════════════════════════════════════════════════════════
    // 输出
    // ═══════════════════════════════════════════════════════════

    /// <summary>默认输出文件名：优先用客户名（人一眼认得出是谁的授权），否则退到序列号 / 类型 + 日期。</summary>
    private static string DefaultFileName(GenerateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Customer))
        {
            return $"授权_{Sanitize(options.Customer)}.lic";
        }

        if (!string.IsNullOrWhiteSpace(options.Serial))
        {
            return $"license_{Sanitize(options.Serial)}.lic";
        }

        return $"license_{options.Kind}_{LicenseIssuer.BeijingDate(DateTimeOffset.UtcNow)}.lic";
    }

    /// <summary>去掉文件名里不允许的字符（客户名是自由文本，可能带 <c>/</c>、<c>:</c> 等）。</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim();

        return string.IsNullOrEmpty(cleaned) ? "unnamed" : cleaned;
    }

    private static void PrintSummary(
        string outputPath,
        LicenseIssuanceRequest request,
        LicenseIssuanceResult result)
    {
        var document = result.Document;

        ConsoleOut.Section("签发完成");
        ConsoleOut.Field("授权类型", $"{LicenseKind.DisplayName(document.Lt)}（{document.Lt}）");
        ConsoleOut.Field(
            "授权对象",
            request.Universal ? "通用（由客户首次导入时自动绑定机器）" : $"{document.Mc}（绑定）");
        ConsoleOut.Field("有效期", DescribeExpiry(document));
        ConsoleOut.Field("签发时刻", BeijingInput.Describe(request.IssuedAtUtc));

        if (document.Ivt > 0)
        {
            ConsoleOut.Field(
                "联网锚点",
                $"{BeijingInput.Describe(DateTimeOffset.FromUnixTimeMilliseconds(document.Ivt))}（ivt）");
        }

        ConsoleOut.Field("序列号", ConsoleOut.OrNone(document.Sn));
        ConsoleOut.Field("厂商标识", ConsoleOut.OrNone(document.Vi));
        ConsoleOut.Field("客户备注", ConsoleOut.OrNone(document.Hn));

        if (document.Ud > 0)
        {
            ConsoleOut.Field(
                "使用截止",
                $"{BeijingInput.Describe(DateTimeOffset.FromUnixTimeMilliseconds(document.Ud))}（须在此之前导入）");
        }

        ConsoleOut.Field("公钥指纹", result.PublicKeyFingerprint);
        ConsoleOut.Field("文件", $"{outputPath}（{new FileInfo(outputPath).Length} 字节）");
        ConsoleOut.Field("文件校验", $"SHA256 {Sha256OfFile(outputPath)}");

        ConsoleOut.Section("自检");
        ConsoleOut.Ok("密码学验签：通过（已按服务端的解码 → 重算载荷 → 验签路径验过）");
        ConsoleOut.Ok("服务端导入规则：通过（按 ImportRules 干跑，字段与规则都收得下）");

        // ⚠️ 上面两条**覆盖不到**"签名公钥是不是服务端内嵌的那把"。服务端只认内嵌公钥：
        //    工具内嵌的私钥与服务端内嵌的公钥不是一对时，文件签名自洽、规则也过，
        //    但到现场一律判「签名无效」。必须在这里把结论明说出来。
        if (result.MatchesVendorPublicKey)
        {
            ConsoleOut.Ok($"签名公钥：与服务端内嵌公钥一致（{VendorPublicKey.Fingerprint}）—— 服务端会收下这份文件。");
        }
        else
        {
            ConsoleOut.Error("签名公钥与服务端内嵌公钥不一致 —— 这份文件服务端会判「签名无效」，无法导入！");
            ConsoleOut.Hint($"本文件由公钥 {result.PublicKeyFingerprint} 签发，而服务端内嵌的是 {VendorPublicKey.Fingerprint}。");
            ConsoleOut.Hint("这说明工具内嵌的私钥与服务端内嵌的公钥不是一对 —— 通常是换钥时只更新了一边。");
            ConsoleOut.Hint("请把新公钥替换进 src/ControlServer.Core/Licensing/VendorPublicKey.cs，");
            ConsoleOut.Hint("重编并重新交付服务端，同时重新构建签发工具。在此之前不要把它发给客户。");
        }

        ConsoleOut.Hint("自检只保证「文件本身没毛病、服务端收得下」；");
        ConsoleOut.Hint("不保证客户机器上一定可用 —— 那还取决于机器码与客户机器是否一致。");

        ConsoleOut.Line();
        ConsoleOut.Line("把 .lic 文件发给客户，由管理员在服务端「授权」页导入。");
    }

    private static string DescribeExpiry(LicenseDocument document)
    {
        if (document.Et > 0)
        {
            return BeijingInput.Describe(DateTimeOffset.FromUnixTimeMilliseconds(document.Et));
        }

        if (!string.IsNullOrEmpty(document.Ed)
            && LicenseEvaluator.TryParseBeijingDate(document.Ed, out var date))
        {
            return $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 当天 23:59:59 结束（北京时间）";
        }

        if (document.Md > 0)
        {
            return $"自绑定时刻起 {document.Md} 天";
        }

        return "永久（不按日期限制）";
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
