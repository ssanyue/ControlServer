using System.Globalization;
using System.Text;
using ControlServer.Core.Services;

namespace ControlServer.Core.Licensing;

/// <summary>
/// 一次签发请求（由签发工具的命令行参数填充）。
///
/// <para>
/// 有效期有三个互斥的入口，对应授权文件里三个不同字段，<b>每次只能给一个</b>：
/// <list type="bullet">
/// <item><description><see cref="ExactExpiryUtcMs"/> → <c>et</c>：精确到秒的到期时刻（优先级最高）。</description></item>
/// <item><description><see cref="ExpiryDate"/> → <c>ed</c>：到期日，当天北京时间 23:59:59 结束。</description></item>
/// <item><description><see cref="MaxDays"/> → <c>md</c>：最大有效天数，起算点由评估器决定。</description></item>
/// </list>
/// 三者全空 = 永久授权（此时<b>必须</b>绑定机器码，见 <see cref="LicenseIssuer"/> 的规则说明）。
/// </para>
/// </summary>
public sealed record LicenseIssuanceRequest
{
    /// <summary>授权类型，取值见 <see cref="LicenseKind"/>（<c>full</c> / <c>trial</c> / <c>temp</c>）。</summary>
    public string Kind { get; init; } = LicenseKind.Full;

    /// <summary>绑定的机器码，任意分隔写法均可（<c>XXXXXXXX</c> / <c>XXXX-XXXX-XXXX-XXXX</c>）。</summary>
    public string MachineCode { get; init; } = string.Empty;

    /// <summary>是否签发通用授权（签发时不绑机器码，由客户首次导入时绑定）。</summary>
    public bool Universal { get; init; }

    /// <summary>签发时刻（UTC）。决定 <c>id</c> 与绑定式授权的 <c>bt</c>。</summary>
    public required DateTimeOffset IssuedAtUtc { get; init; }

    /// <summary>
    /// 联网校对时刻（UTC），写进 <c>ivt</c>。
    /// <para>
    /// <b>必填</b>：<c>ivt</c> 是整套防线的关键字段，缺它的授权会被服务端直接拒绝。
    /// 签发工具的正常取值是 SNTP 查到的服务器时间。
    /// </para>
    /// </summary>
    public DateTimeOffset? AnchorUtc { get; init; }

    /// <summary>精确到期时刻（UTC 毫秒戳）；<c>0</c> = 不采用此形式。</summary>
    public long ExactExpiryUtcMs { get; init; }

    /// <summary>到期日（<c>yyyyMMdd</c> 北京日期）；空 = 不采用此形式。</summary>
    public string ExpiryDate { get; init; } = string.Empty;

    /// <summary>最大有效天数；<c>0</c> = 不采用此形式。</summary>
    public int MaxDays { get; init; }

    /// <summary>通用授权的"最迟可导入绑定"时刻（UTC）；非通用授权忽略。</summary>
    public DateTimeOffset? UseDeadlineUtc { get; init; }

    /// <summary>序列号（仅展示）。</summary>
    public string Serial { get; init; } = string.Empty;

    /// <summary>厂商标识（仅展示）。</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>客户备注（仅展示）。</summary>
    public string Customer { get; init; } = string.Empty;

    /// <summary>时钟容差，仅用于签发自检时对齐服务端的判定口径。</summary>
    public TimeSpan ClockTolerance { get; init; } = LicenseEvaluator.DefaultClockTolerance;
}

/// <summary>签发结果。</summary>
/// <param name="Document">已签名的文档（<c>Sig</c> 已填充）。</param>
/// <param name="Encoded">可直接分发的 Base64 文本。</param>
/// <param name="PublicKeyPem">与签名私钥配对的公钥 PEM（从私钥现推，供替换服务端内嵌公钥）。</param>
/// <param name="PublicKeyFingerprint">公钥指纹（与 <see cref="VendorPublicKey.Fingerprint"/> 可直接比对）。</param>
public sealed record LicenseIssuanceResult(
    LicenseDocument Document,
    string Encoded,
    string PublicKeyPem,
    string PublicKeyFingerprint)
{
    /// <summary>
    /// 本次签名所用的公钥，是否<b>就是服务端内嵌的那把</b>。
    ///
    /// <para>
    /// <b>这一项决定"这份 .lic 服务端收不收"，而它不在任何自检的覆盖范围内</b>：
    /// 服务端只认 <see cref="VendorPublicKey"/> 里内嵌的公钥。
    /// 为 <c>false</c> 时文件本身完全合法（签名自洽、字段齐备、业务规则也干跑通过），
    /// 但<b>发给任何现有服务端都会被判「签名无效」</b>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 这条曾经是真实事故：签发工具拿别的私钥签了一份授权，自带的两道自检全部报"通过"
    /// （见 <c>DryRunImport</c> 的说明），用户于是把文件发给了客户，客户导入时才发现签名无效。
    /// 因此<b>签发工具必须把本项结果显示出来</b>，不能只显示一个孤零零的指纹让人自己去比。
    /// </para>
    /// </summary>
    public bool MatchesVendorPublicKey =>
        string.Equals(PublicKeyFingerprint, VendorPublicKey.Fingerprint, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 授权签发：把 <see cref="LicenseIssuanceRequest"/> 变成一份<b>服务端一定收得下</b>的授权文件。
///
/// <para>
/// <b>为什么规则放在 Core 而不是签发工具里</b>：这些规则（永久必须绑机器码、通用必须有使用截止、
/// 有效期三选一…）与 <see cref="LicenseImportRules"/>、<see cref="LicenseEvaluator"/> 是一套东西的两面 ——
/// 签发侧防止"造出收不下的文件"，导入侧防止"收下不该收的文件"。放在一起，
/// 两边共用同一个评估器，不可能出现"签发说没问题、导入说有毛病"的分裂。
/// </para>
///
/// <para>
/// <b>两道自检</b>，各挡一类失败，缺一不可：
/// <list type="number">
/// <item><description><b>密码学自检</b>（<see cref="LicenseSigner.SignWithSelfCheck"/>）：签名与载荷是否自洽。</description></item>
/// <item><description><b>业务自检</b>（<see cref="LicenseImportRules"/> 干跑）：这份文件服务端愿不愿意收。</description></item>
/// </list>
/// 第一道能过而第二道过不了的例子：签了一份"到期时间已经过去"的授权 —— 签名完美，导入必被拒。
/// </para>
/// </summary>
public static class LicenseIssuer
{
    /// <summary>通用授权在签发时刻还没有机器码，干跑业务自检时用的占位值。</summary>
    public const string UnboundMachineCodePlaceholder = "0000-0000-0000-0000";

    /// <summary>
    /// 按请求签发一份授权。
    /// </summary>
    /// <param name="request">签发请求。</param>
    /// <param name="privateKeyPem">PKCS#8 私钥 PEM。</param>
    /// <exception cref="LicenseValidationException">请求本身不合法，或签出自检未通过。</exception>
    public static LicenseIssuanceResult Issue(LicenseIssuanceRequest request, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(request);

        var document = BuildDocument(request);

        var (encoded, publicKeyPem, fingerprint) = LicenseSigner.SignWithSelfCheck(document, privateKeyPem);

        DryRunImport(document, request);

        return new LicenseIssuanceResult(document, encoded, publicKeyPem, fingerprint);
    }

    /// <summary>
    /// 只做规则校验与文档构造，不签名。
    /// <para>单独暴露是为了让签发工具能"先校验、后签名"，也便于单测聚焦在规则上。</para>
    /// </summary>
    /// <exception cref="LicenseValidationException">规则不满足。</exception>
    public static LicenseDocument BuildDocument(LicenseIssuanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ── ① 类型 ──
        if (!LicenseKind.IsValid(request.Kind))
        {
            throw new LicenseValidationException(
                $"授权类型「{request.Kind}」无法识别，只能是 {string.Join(" / ", LicenseKind.All)} 之一。");
        }

        // ── ② 绑定方式 ──
        var machineCode = string.Empty;

        if (request.Universal)
        {
            if (!string.IsNullOrWhiteSpace(request.MachineCode))
            {
                throw new LicenseValidationException(
                    "「通用授权」与「绑定机器码」不能同时使用：通用授权在签发时就是要留空机器码的。");
            }
        }
        else
        {
            if (!TryNormalizeMachineCode(request.MachineCode, out machineCode))
            {
                throw new LicenseValidationException(
                    $"机器码「{request.MachineCode}」不是合法形式，应为 16 位十六进制（如 A1B2-C3D4-E5F6-0718）。"
                    + "请让客户从服务端「授权」页原样复制，不要手抄。");
            }
        }

        // ── ③ 有效期三选一 ──
        var expiryForms = 0;
        if (request.ExactExpiryUtcMs > 0) expiryForms++;
        if (!string.IsNullOrWhiteSpace(request.ExpiryDate)) expiryForms++;
        if (request.MaxDays > 0) expiryForms++;

        if (expiryForms > 1)
        {
            throw new LicenseValidationException(
                "有效期只能指定一种形式（精确时刻 / 到期日 / 天数），不能同时给多个 —— 否则哪条生效全靠字段优先级，"
                + "签发时看不出来，到客户那里才发现不是自己以为的期限。");
        }

        if (request.MaxDays < 0)
        {
            throw new LicenseValidationException("有效天数不能为负数。");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpiryDate)
            && !LicenseEvaluator.TryParseBeijingDate(request.ExpiryDate, out _))
        {
            throw new LicenseValidationException(
                $"到期日「{request.ExpiryDate}」无法解析，应为 yyyyMMdd 形式（如 20271008）。");
        }

        // ── ④ 试用必须有期限 ──
        if (expiryForms == 0 && request.Kind == LicenseKind.Trial)
        {
            throw new LicenseValidationException(
                "试用授权必须指定有效期（--expire 或 --days）——没有期限的「试用」在服务端会被判为错误，"
                + "而且绝不能顺势当成永久授权放行。");
        }

        // ── ⑤⑥ 通用授权的额外约束 ──
        // 两条一起查、一起报：它们经常同时不满足（`--universal` 后面什么都没跟上），
        // 一次只报一条会让操作者改成一处、再撞上另一处，白跑一轮。
        if (request.Universal)
        {
            var problems = new List<string>();

            if (request.UseDeadlineUtc is null)
            {
                problems.Add(
                    "必须指定 --deadline（最迟可导入绑定时间）。这是通用授权唯一的时效约束："
                    + "只靠「到期时间」管不住「什么时候才第一次用」，一份签发后躺了两年的通用授权仍会在那时生效。");
            }

            if (expiryForms == 0)
            {
                problems.Add(
                    "不允许「通用 + 永久」。通用授权没有期限约束就等于「一份文件、任何机器、永远有效」，"
                    + "丢失即失控。请指定 --expire 或 --days，或改为绑定机器码的永久授权。");
            }

            if (problems.Count == 1)
            {
                throw new LicenseValidationException(problems[0]);
            }

            if (problems.Count > 1)
            {
                throw new LicenseValidationException(
                    "通用授权不满足以下约束：\n  - " + string.Join("\n  - ", problems));
            }
        }

        if (!request.Universal && request.UseDeadlineUtc is not null)
        {
            throw new LicenseValidationException(
                "--deadline 只对通用授权有意义（它约束的是「首次导入绑定」这个动作）；"
                + "绑定式授权请在导入时校验，不需要此字段。");
        }

        // ── ⑦ 联网锚点 ──
        if (request.AnchorUtc is null || request.AnchorUtc.Value.ToUnixTimeMilliseconds() <= 0)
        {
            throw new LicenseValidationException(
                "缺少联网校对时间（ivt）。这是授权文件的关键字段，缺失的文件会被服务端直接拒绝。"
                + "签发工具会尝试自动获取网络时间；若本机无法联网，请用 --anchor 显式指定。");
        }

        var anchor = request.AnchorUtc.Value;

        if (anchor > request.IssuedAtUtc + request.ClockTolerance)
        {
            throw new LicenseValidationException(
                $"联网校对时间（{BeijingTime.Format(anchor)}）晚于本机当前时间（{BeijingTime.Format(request.IssuedAtUtc)}），"
                + "签出来的授权会因「本机时间早于授权锚点」被客户机器拒绝。请检查本机时钟。");
        }

        // ── 构造文档 ──
        var document = new LicenseDocument
        {
            V = LicenseDocument.CurrentVersion,
            Mc = machineCode,
            Lt = request.Kind,
            Id = BeijingDate(request.IssuedAtUtc),
            Ed = request.ExpiryDate,
            Et = request.ExactExpiryUtcMs,
            Md = request.MaxDays,
            Sn = request.Serial,
            Vi = request.Vendor,
            Hn = request.Customer,
            Ub = request.Universal ? 1 : 0,
            Ud = request.UseDeadlineUtc?.ToUnixTimeMilliseconds() ?? 0,
            // 绑定式授权的 bt = 签发时刻（见 LicenseDocument.Bt 的注释）；
            // 通用授权此时尚未绑定，留 0。
            Bt = request.Universal ? 0 : request.IssuedAtUtc.ToUnixTimeMilliseconds(),
            Ivt = anchor.ToUnixTimeMilliseconds(),
        };

        // ── ⑧ 跨字段校验（借评估器算出的真实到期时刻，不另写一套） ──
        var expiresAt = LicenseEvaluator.ResolveExpiry(document);

        if (expiresAt is not null && request.IssuedAtUtc > expiresAt.Value)
        {
            throw new LicenseValidationException(
                $"指定的到期时间（{BeijingTime.Format(expiresAt.Value)}）早于签发时刻"
                + $"（{BeijingTime.Format(request.IssuedAtUtc)}）：签出去就是一份已过期的授权。");
        }

        if (request.UseDeadlineUtc is not null)
        {
            if (request.UseDeadlineUtc.Value <= request.IssuedAtUtc)
            {
                throw new LicenseValidationException(
                    $"使用截止时间（{BeijingTime.Format(request.UseDeadlineUtc.Value)}）不晚于签发时刻："
                    + "这样的通用授权客户当天就导不进去。");
            }

            if (expiresAt is not null && request.UseDeadlineUtc.Value >= expiresAt.Value)
            {
                throw new LicenseValidationException(
                    $"使用截止时间（{BeijingTime.Format(request.UseDeadlineUtc.Value)}）必须早于授权到期时间"
                    + $"（{BeijingTime.Format(expiresAt.Value)}）：否则「最迟导入时间」落在失效之后，这条约束形同虚设。");
            }
        }

        return document;
    }

    /// <summary>
    /// 把任意书写形式的机器码规范成 <c>XXXX-XXXX-XXXX-XXXX</c>（大写）。
    /// <para>
    /// 机器码会被原样写进签名载荷，格式漂移会让客户拿着"看起来一样"的码导入失败。
    /// 归一化把"抄写差异"消除在签发侧，而不是留给现场去猜。
    /// </para>
    /// </summary>
    public static bool TryNormalizeMachineCode(string? input, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(input)) return false;

        var digits = new StringBuilder(16);

        foreach (var ch in input)
        {
            if (ch is '-' or ' ' or '\t') continue;
            if (!Uri.IsHexDigit(ch)) return false;
            digits.Append(ch);
        }

        if (digits.Length != 16) return false;

        var hex = digits.ToString().ToUpperInvariant();

        normalized = string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
        return true;
    }

    /// <summary>北京日期 <c>yyyyMMdd</c>。</summary>
    public static string BeijingDate(DateTimeOffset value)
        => BeijingTime.ToBeijing(value).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 由"本机当前时刻"与"联网锚点"确定<b>实际写进授权的签发时刻</b>。
    ///
    /// <para>
    /// 规则只有一条，但漏掉它的后果很隐蔽：本机时钟若比网络时间<b>慢</b>超过时钟容差，
    /// 用本机时刻当签发时刻会得到一个<b>早于 <c>ivt</c> 锚点</b>的授权 —— 服务端读状态时
    /// 会把它判成"时间被回拨"，于是<b>刚签出来的文件在自己这里就验不过</b>。
    /// 因此锚点明显晚于本机时改用锚点。
    /// </para>
    ///
    /// <para>
    /// <b>为什么放在 Core</b>：服务端评估侧与签发侧必须共用同一套时间口径，
    /// 否则"签发说没问题、导入说有毛病"的分裂会再次出现。CLI 与 GUI 都调用本方法，
    /// 不允许任何一端自己再写一遍这个比较。
    /// </para>
    /// </summary>
    /// <param name="localNow">本机当前时刻（UTC）。</param>
    /// <param name="anchor">联网锚点时刻（UTC）。</param>
    /// <param name="clockTolerance">时钟容差；锚点晚于本机加容差才认为本机钟偏慢。</param>
    /// <returns>实际应采用的签发时刻。</returns>
    public static DateTimeOffset ResolveIssuedAt(
        DateTimeOffset localNow,
        DateTimeOffset anchor,
        TimeSpan clockTolerance)
        => anchor > localNow + clockTolerance ? anchor : localNow;

    /// <summary>
    /// 业务自检：按服务端的导入规则干跑一遍。
    ///
    /// <para>
    /// <b>这道自检覆盖不到的两件事，必须在签发报告里单独交代</b> —— 历史上正是漏了它们才出事故：
    /// <list type="number">
    /// <item><description>
    /// <b>签名用的公钥是不是服务端内嵌的那把</b>。<c>signatureValid</c> 这里传 <c>true</c>，
    /// 因为文档就是本进程刚签的、密码学自洽（那道检查在 <see cref="LicenseSigner.SignWithSelfCheck"/> 里）。
    /// 但服务端验签用的是<b>它自己内嵌</b>的公钥 —— "签名自洽"与"服务端认不认"是两件事。
    /// 判定见 <see cref="LicenseIssuanceResult.MatchesVendorPublicKey"/>。
    /// </description></item>
    /// <item><description>
    /// <b>机器码是不是真的是目标机器的</b>。干跑拿文档自己的 <c>mc</c> 当本机机器码喂进去，
    /// 因此永远自洽；签给别台机器的授权天然通不过"自我对照"，只能靠人核对。
    /// </description></item>
    /// </list>
    /// </para>
    /// </summary>
    private static void DryRunImport(LicenseDocument document, LicenseIssuanceRequest request)
    {
        var probeMachineCode = string.IsNullOrWhiteSpace(document.Mc)
            ? UnboundMachineCodePlaceholder
            : document.Mc;

        var result = LicenseImportRules.Check(
            document,
            // 见上方说明：只能传 true，本方法不负责"公钥是否厂商内嵌"这一条。
            signatureValid: true,
            localMachineCode: probeMachineCode,
            nowUtc: request.IssuedAtUtc,
            binding: null,
            clockTolerance: request.ClockTolerance);

        if (!result.Ok)
        {
            throw new LicenseValidationException(
                $"签发自检失败：按服务端的导入规则，这份授权会被拒（{result.Status}）—— {result.Message}");
        }
    }
}
