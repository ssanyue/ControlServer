using System.Globalization;
using ControlServer.Core.Services;

namespace ControlServer.Core.Licensing;

/// <summary>
/// 授权判定的输入。
/// </summary>
/// <param name="Document">已解析的授权文档；无文件为 <c>null</c>。</param>
/// <param name="Binding">通用授权的绑定侧车；没有为 <c>null</c>。</param>
/// <param name="LocalTrial">本机自动试用记录；没有为 <c>null</c>。仅在<b>没有授权文件</b>时生效。</param>
/// <param name="SignatureValid">验签结果。<b>解析成功不等于签名有效</b>，两者必须分开传。</param>
/// <param name="SignatureError">验签失败原因（供展示）。</param>
/// <param name="LocalMachineCode">本机机器码（<c>XXXX-XXXX-XXXX-XXXX</c>）。</param>
/// <param name="NowUtc">当前时刻（<b>已含 NTP 校正</b>，即 <c>CorrectedTimeProvider</c> 给的 UTC）。</param>
/// <param name="ClockTolerance">时钟容差：判定"系统时间是否早于联网锚点"时允许的回拨量。</param>
public sealed record LicenseEvaluationInput(
    LicenseDocument? Document,
    LicenseBinding? Binding,
    bool SignatureValid,
    string? SignatureError,
    string LocalMachineCode,
    DateTimeOffset NowUtc,
    TimeSpan ClockTolerance)
{
    /// <summary>本机自动试用记录（可选）。见 <see cref="LocalTrialRecord"/> 的类注释。</summary>
    public LocalTrialRecord? LocalTrial { get; init; }

    /// <summary>
    /// 读取授权文件本身失败的原因（文件在，但读不出合法授权：解析失败 / IO 异常）。
    /// <para>
    /// ⚠️ 与"没有授权文件"是**两件事**，必须分开传：
    /// 前者要报错（现场有人把文件弄坏了，得让人知道），后者才轮到本机试用兜底。
    /// 若把读失败也当成"没有文件"，一份损坏的授权会被静默忽略、试用悄然顶上 ——
    /// 现场看到的是"我明明导入了授权，怎么还显示试用中"。
    /// </para>
    /// </summary>
    public string? ReadError { get; init; }
}

/// <summary>
/// 授权评估器：<b>纯函数</b>，不碰文件、不碰时钟、不碰网络。
///
/// <para>
/// 输入的全部要素（文档 / 绑定 / 机器码 / 当前时刻 / 容差）都经参数传入，
/// 因此"试用最后一天 23:59:59"这类边界可以在单测里精确构造，而不必等到真实时间走到那一刻。
/// 这与 <c>TimerSchedulerService</c> 注入 <c>TimeProvider</c> 是同一个理由。
/// </para>
///
/// <para>判定顺序与参照实现一致，且<b>顺序本身有语义</b>（见各步骤注释）。</para>
/// </summary>
public static class LicenseEvaluator
{
    /// <summary>时钟容差的默认值。参照实现用 2 分钟；服务器时钟漂移远小于此。</summary>
    public static readonly TimeSpan DefaultClockTolerance = TimeSpan.FromMinutes(2);

    public static LicenseState Evaluate(LicenseEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ── ① 无授权文件 ──
        // 此时唯一可能让设备"还能用"的是本机自动试用。注意顺序：
        // **有授权文件时，本机试用一律不参与判定** —— 否则一份已过期的正式授权
        // 会因为"旁边还躺着一份试用记录"而继续放行，等于过期无效。
        if (input.Document is null)
        {
            // 文件在、但读不成合法授权 → 报错，且**不**退到本机试用。
            if (!string.IsNullOrWhiteSpace(input.ReadError))
            {
                return LicenseState.Damaged(input.ReadError);
            }

            return EvaluateLocalTrial(input);
        }

        var document = input.Document;

        // ── ② 验签 ──
        // 必须排在所有字段校验之前：签名无效时，文档里的任何字段都是不可信输入，
        // 拿它去算"剩余几天"反而会把伪造内容显示给用户。
        if (!input.SignatureValid)
        {
            var message = string.IsNullOrWhiteSpace(input.SignatureError)
                ? "授权文件签名无效或已被篡改。"
                : $"授权文件签名无效或已被篡改：{input.SignatureError}";

            return Failure(LicenseStatus.Error, document.Lt, message);
        }

        // ── ③ 版本 ──
        if (document.V != LicenseDocument.CurrentVersion)
        {
            return Failure(
                LicenseStatus.Error,
                document.Lt,
                $"授权文件版本为 {document.V}，本服务只支持版本 {LicenseDocument.CurrentVersion}。请向供应方索取新版本授权。");
        }

        // ── ④ 类型 ──
        if (!LicenseKind.IsValid(document.Lt))
        {
            return Failure(LicenseStatus.Error, document.Lt, $"授权类型「{document.Lt}」无法识别。");
        }

        // ── ⑤ 机器码绑定 ──
        // 通用授权签发时 mc 为空，绑定结果在侧车（见 LicenseBinding 的注释）。
        var boundMachineCode = !string.IsNullOrWhiteSpace(document.Mc)
            ? document.Mc
            : input.Binding?.MachineCode;

        if (string.IsNullOrWhiteSpace(boundMachineCode))
        {
            return Failure(
                LicenseStatus.Error,
                document.Lt,
                document.Ub == 1
                    ? "这是一份通用授权，但尚未在本机完成绑定。请通过「导入授权」完成首次绑定。"
                    : "授权文件未绑定任何机器码。");
        }

        if (!MachineCodeEquals(boundMachineCode, input.LocalMachineCode))
        {
            return Failure(
                LicenseStatus.Error,
                document.Lt,
                $"授权文件绑定的是机器 {boundMachineCode}，本机机器码为 {input.LocalMachineCode}。请索取与本机匹配的授权文件。");
        }

        // ── ⑥ 联网锚点 ivt ──
        // 这一步挡的是"改文件延长授权"：手工改出来的文件即使签名合法（历史上对称密钥时代确实可能），
        // 也会因为没有 ivt 而被拒。
        if (document.Ivt <= 0)
        {
            return Failure(LicenseStatus.Error, document.Lt, "授权文件缺少联网校对记录（ivt），可能已被篡改。");
        }

        var anchor = DateTimeOffset.FromUnixTimeMilliseconds(document.Ivt);

        if (input.NowUtc + input.ClockTolerance < anchor)
        {
            return Failure(
                LicenseStatus.Error,
                document.Lt,
                $"本机时间（{BeijingTime.Format(input.NowUtc)}）早于授权联网校对时间（{BeijingTime.Format(anchor)}），"
                + "可能有人把系统时间往回调过。请先校正本机时间。");
        }

        // ── ⑦ 有效期 ──
        var expiresAt = ResolveExpiry(document);

        // 试用授权必须带期限。没期限的"试用"要么是签发工具的 bug，要么是被人改过 ——
        // 一律按错误处理，绝不能顺势放行成永久授权。
        if (expiresAt is null && document.Lt == LicenseKind.Trial)
        {
            return Failure(LicenseStatus.Error, document.Lt, "试用授权未指定有效期。");
        }

        if (expiresAt is null)
        {
            return new LicenseState(
                LicenseStatus.Authorized,
                document.Lt,
                LicenseState.PermanentRemainingDays,
                null,
                DescribePerpetual(document),
                string.Empty);
        }

        var expiresBeijing = BeijingTime.ToBeijing(expiresAt.Value);

        if (input.NowUtc > expiresAt.Value)
        {
            var daysAgo = (int)Math.Floor((input.NowUtc - expiresAt.Value).TotalDays);

            return new LicenseState(
                LicenseStatus.Expired,
                document.Lt,
                0,
                expiresBeijing,
                string.Concat(
                    LicenseKind.DisplayName(document.Lt),
                    "已于 ",
                    BeijingTime.Format(expiresBeijing),
                    " 过期",
                    daysAgo > 0 ? $"（已过期 {daysAgo} 天）" : string.Empty,
                    "。"),
                "授权已过期，请导入新的授权文件。");
        }

        // 剩余天数向上取整：还剩 3 小时应显示"1 天"而不是"0 天"（后者会被读成"已过期"）。
        var remaining = Math.Max(0, (int)Math.Ceiling((expiresAt.Value - input.NowUtc).TotalDays));

        return new LicenseState(
            document.Lt == LicenseKind.Trial ? LicenseStatus.Trial : LicenseStatus.Authorized,
            document.Lt,
            remaining,
            expiresBeijing,
            string.Concat(
                LicenseKind.DisplayName(document.Lt),
                "有效，剩余 ",
                remaining.ToString(CultureInfo.InvariantCulture),
                " 天（至 ",
                BeijingTime.Format(expiresBeijing),
                "）。"),
            string.Empty);
    }

    /// <summary>
    /// 从文档解析到期时刻（UTC 时刻，但承载北京墙钟用于展示）。
    /// <para>优先级：<c>et</c>（精确时刻） &gt; <c>ed</c>（当日北京时间 23:59:59） &gt; <c>md</c>（最大天数兜底）。</para>
    /// </summary>
    public static DateTimeOffset? ResolveExpiry(LicenseDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Et > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(document.Et);
        }

        if (TryParseBeijingDate(document.Ed, out var expireDate))
        {
            // 到期日的当天末刻：客户直觉里"10 月 8 日到期"意味着 10 月 8 日一整天都还能用。
            return new DateTimeOffset(
                expireDate.Year, expireDate.Month, expireDate.Day, 23, 59, 59, BeijingTime.Offset);
        }

        if (document.Md > 0)
        {
            // 纯天数授权（无 ed/et）：起算点取"绑定时刻"，没有就退回签发日。
            var start = document.Bt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(document.Bt)
                : TryParseBeijingDate(document.Id, out var issued)
                    ? new DateTimeOffset(issued.Year, issued.Month, issued.Day, 0, 0, 0, BeijingTime.Offset)
                    : (DateTimeOffset?)null;

            return start?.AddDays(document.Md);
        }

        return null;
    }

    /// <summary>解析 <c>yyyyMMdd</c> 形式的北京日期。</summary>
    public static bool TryParseBeijingDate(string? text, out DateTime date)
        => DateTime.TryParseExact(
            text,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    /// <summary>把 UTC 毫秒戳转成可读的北京时间文本；<c>0</c> 返回空串。</summary>
    public static string FormatUtcMs(long utcMs)
        => utcMs <= 0 ? string.Empty : BeijingTime.Format(DateTimeOffset.FromUnixTimeMilliseconds(utcMs));

    /// <summary>
    /// 机器码比较：忽略大小写与首尾空白。
    /// <para>
    /// 机器码是十六进制串，客户抄写时大小写混杂很常见；比较时过于严格
    /// 只会制造"明明是这台机器却说不匹配"的无效工单。
    /// </para>
    /// </summary>
    public static bool MachineCodeEquals(string? left, string? right)
        => string.Equals(
            left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 本机自动试用的判定（只在没有厂商授权文件时走这里）。
    /// <para>
    /// 这份记录不带签名 —— 它被定义为「本地策略状态」而不是凭证，理由见
    /// <see cref="LocalTrialRecord"/> 的类注释（简单说：给它套假签名不增加任何防护，
    /// 因为能改它的人同样能删掉整个数据目录）。
    /// </para>
    /// </summary>
    private static LicenseState EvaluateLocalTrial(LicenseEvaluationInput input)
    {
        var trial = input.LocalTrial;

        if (trial is null)
        {
            const string message = "本机尚未导入授权文件。";
            return new LicenseState(
                LicenseStatus.NotAuthorized, string.Empty, LicenseState.UnknownRemainingDays, null, message, message);
        }

        // 换了机器（或机器码因硬件变更而改变）就不认这份试用 ——
        // 与厂商授权的绑定语义保持一致，避免"换台机器试用期还在"的错觉。
        if (!MachineCodeEquals(trial.MachineCode, input.LocalMachineCode))
        {
            var message = $"本机试用记录属于机器 {trial.MachineCode}，本机机器码为 {input.LocalMachineCode}。";
            return new LicenseState(
                LicenseStatus.Error, LicenseKind.Trial, LicenseState.UnknownRemainingDays, null, message, message);
        }

        if (!TryParseBeijingDate(trial.ExpiresOn, out var expiryDate))
        {
            const string message = "本机试用记录的到期日无法解析，可能已损坏。";
            return new LicenseState(
                LicenseStatus.Error, LicenseKind.Trial, LicenseState.UnknownRemainingDays, null, message, message);
        }

        var expiresAt = new DateTimeOffset(
            expiryDate.Year, expiryDate.Month, expiryDate.Day, 23, 59, 59, BeijingTime.Offset);

        if (input.NowUtc > expiresAt)
        {
            return new LicenseState(
                LicenseStatus.Expired,
                LicenseKind.Trial,
                0,
                expiresAt,
                $"本机试用已于 {BeijingTime.Format(expiresAt)} 结束。",
                "试用期已结束，请导入正式授权文件。");
        }

        var remaining = Math.Max(0, (int)Math.Ceiling((expiresAt - input.NowUtc).TotalDays));

        return new LicenseState(
            LicenseStatus.Trial,
            LicenseKind.Trial,
            remaining,
            expiresAt,
            $"本机试用中，剩余 {remaining} 天（至 {BeijingTime.Format(expiresAt)}）"
            + $"（自 {FormatUtcMs(trial.IssuedAtUtcMs)} 起 {trial.Days} 天）。",
            string.Empty)
        {
            IsLocalTrial = true,
        };
    }

    private static LicenseState Failure(LicenseStatus status, string kind, string message)
        => new(status, kind, LicenseState.UnknownRemainingDays, null, message, message);

    private static string DescribePerpetual(LicenseDocument document)
    {
        var parts = new List<string> { $"{LicenseKind.DisplayName(document.Lt)}（永久）" };

        if (!string.IsNullOrWhiteSpace(document.Sn)) parts.Add($"序列号 {document.Sn}");
        if (!string.IsNullOrWhiteSpace(document.Hn)) parts.Add($"客户 {document.Hn}");

        return string.Join(" · ", parts) + "。";
    }
}
