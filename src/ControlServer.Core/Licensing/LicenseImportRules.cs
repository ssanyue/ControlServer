using ControlServer.Core.Services;

namespace ControlServer.Core.Licensing;

/// <summary>导入授权的结果码。</summary>
public enum LicenseImportStatus
{
    /// <summary>导入成功，且授权当前可用。</summary>
    Ok,

    /// <summary>文本为空 / 不是 Base64 / 不是合法 JSON / 缺 sig。</summary>
    Malformed,

    /// <summary>验签失败。</summary>
    SignatureInvalid,

    /// <summary>文件版本不受支持。</summary>
    UnsupportedVersion,

    /// <summary>授权类型无法识别。</summary>
    UnknownKind,

    /// <summary>绑定的是别的机器。</summary>
    MachineMismatch,

    /// <summary>导入时已经过期。</summary>
    Expired,

    /// <summary>通用授权已超过可导入绑定的使用截止时间（<c>ud</c>）。</summary>
    DeadlineExceeded,

    /// <summary>缺联网锚点 <c>ivt</c>（手工拼出来的文件会走到这里）。</summary>
    MissingNetworkAnchor,

    /// <summary>本机时间早于授权里的联网校对时间，或联网时间不可得。</summary>
    ClockUnusable,
}

/// <summary>
/// 导入授权的结果。
/// </summary>
/// <param name="Status">结果码。</param>
/// <param name="Message">面向用户的一句话说明。</param>
/// <param name="Document">解析并<b>校验通过</b>的文档（含需要落盘的绑定信息）；失败为 <c>null</c>。</param>
/// <param name="Binding">本次需要在侧车里落盘的绑定；非通用授权或失败时为 <c>null</c>。</param>
/// <param name="State">校验通过后的授权状态；失败为 <c>null</c>。</param>
public sealed record LicenseImportResult(
    LicenseImportStatus Status,
    string Message,
    LicenseDocument? Document,
    LicenseBinding? Binding,
    LicenseState? State)
{
    /// <summary>
    /// 是否导入成功。
    /// <para>
    /// ⚠️ 这里用的是<b>等于枚举值</b>的判定（白名单），不是 <c>Status &lt;= Ok</c> 之类的范围比较 ——
    /// 参照实现在这里翻过车，把"已过期""异常"也当成了成功。范围比较在本枚举上<b>没有任何语义</b>，
    /// 只是因为枚举值恰好有序才"看起来能用"，属于典型的巧合可靠。
    /// </para>
    /// </summary>
    public bool Ok => Status == LicenseImportStatus.Ok;

    internal static LicenseImportResult Fail(LicenseImportStatus status, string message)
        => new(status, message, null, null, null);
}

/// <summary>
/// 导入授权时的业务规则（纯函数）。
///
/// <para>
/// 与 <see cref="LicenseEvaluator"/> 分开，是因为两者的判定时机不同：
/// <b>评估</b>回答"这份已落盘的授权现在还能不能用"（每次读状态都跑）；
/// <b>导入</b>回答"这份新文件允不允许收下"（只在导入那一刻跑）。
/// 混在一起会让"通用授权超过 ud 之后仍在本机正常使用"这类正常场景被误杀。
/// </para>
/// </summary>
public static class LicenseImportRules
{
    /// <summary>
    /// 校验一份待导入的授权。
    /// </summary>
    /// <param name="document">已解析的文档。<b>调用方必须已完成验签</b>并把结果放进 <paramref name="signatureValid"/>。</param>
    /// <param name="signatureValid">验签是否通过。</param>
    /// <param name="localMachineCode">本机机器码。</param>
    /// <param name="nowUtc">当前时刻（含 NTP 校正的 UTC）。</param>
    /// <param name="binding">已有的绑定侧车（重复导入时要判断是否换机器）。</param>
    /// <param name="clockTolerance">时钟容差。</param>
    /// <param name="refreshAnchorIvt">
    /// 是否用本机当前的联网时间刷新 <c>ivt</c>。
    /// <para>
    /// ⚠️ <b>必须为 <c>false</c></b>：<c>ivt</c> 在签名载荷里，改它等于改签名内容、签名立刻失效。
    /// 这个参数保留在签名里是为了把这条约束<b>写进代码</b>而不是写进注释 ——
    /// 将来若有人想"顺手刷新一下锚点"，会先看到这里。
    /// </para>
    /// </param>
    public static LicenseImportResult Check(
        LicenseDocument document,
        bool signatureValid,
        string localMachineCode,
        DateTimeOffset nowUtc,
        LicenseBinding? binding,
        TimeSpan clockTolerance,
        bool refreshAnchorIvt = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = refreshAnchorIvt;

        if (!signatureValid)
        {
            return LicenseImportResult.Fail(
                LicenseImportStatus.SignatureInvalid, "授权文件签名无效或已被篡改，无法导入。");
        }

        if (document.V != LicenseDocument.CurrentVersion)
        {
            return LicenseImportResult.Fail(
                LicenseImportStatus.UnsupportedVersion,
                $"授权文件版本为 {document.V}，本服务只支持版本 {LicenseDocument.CurrentVersion}。");
        }

        if (!LicenseKind.IsValid(document.Lt))
        {
            return LicenseImportResult.Fail(
                LicenseImportStatus.UnknownKind, $"授权类型「{document.Lt}」无法识别。");
        }

        // ── 联网锚点 ──
        // 导入路径上也必须卡这一条：否则"手工拼一份带合法签名的文件"就能绕过。
        if (document.Ivt <= 0)
        {
            return LicenseImportResult.Fail(
                LicenseImportStatus.MissingNetworkAnchor, "授权文件缺少联网校对记录（ivt），可能已被篡改。");
        }

        var anchor = DateTimeOffset.FromUnixTimeMilliseconds(document.Ivt);

        if (nowUtc + clockTolerance < anchor)
        {
            return LicenseImportResult.Fail(
                LicenseImportStatus.ClockUnusable,
                $"本机时间（{BeijingTime.Format(nowUtc)}）早于授权联网校对时间（{BeijingTime.Format(anchor)}）。"
                + "请先校正本机时间再导入。");
        }

        // ── 绑定 ──
        var isUniversal = document.Ub == 1 && string.IsNullOrWhiteSpace(document.Mc);
        LicenseBinding? newBinding = null;

        if (isUniversal)
        {
            // 通用授权：只能绑一次。已经绑过就必须还是同一台机器。
            if (binding is not null && !LicenseEvaluator.MachineCodeEquals(binding.MachineCode, localMachineCode))
            {
                return LicenseImportResult.Fail(
                    LicenseImportStatus.MachineMismatch,
                    $"该通用授权已在本机绑定到机器 {binding.MachineCode}，与本机机器码 {localMachineCode} 不一致。"
                    + "如需换机使用，请向供应方索取新的授权文件。");
            }

            if (document.Ud > 0 && nowUtc > DateTimeOffset.FromUnixTimeMilliseconds(document.Ud))
            {
                return LicenseImportResult.Fail(
                    LicenseImportStatus.DeadlineExceeded,
                    $"已超过该通用授权允许导入绑定的截止时间（{LicenseEvaluator.FormatUtcMs(document.Ud)}），无法再导入。");
            }

            newBinding = binding ?? new LicenseBinding(localMachineCode, nowUtc.ToUnixTimeMilliseconds());
        }
        else
        {
            var boundMachineCode = !string.IsNullOrWhiteSpace(document.Mc)
                ? document.Mc
                : binding?.MachineCode;

            if (string.IsNullOrWhiteSpace(boundMachineCode))
            {
                return LicenseImportResult.Fail(
                    LicenseImportStatus.MachineMismatch, "授权文件未绑定任何机器码，无法导入。");
            }

            if (!LicenseEvaluator.MachineCodeEquals(boundMachineCode, localMachineCode))
            {
                return LicenseImportResult.Fail(
                    LicenseImportStatus.MachineMismatch,
                    $"授权文件绑定的是机器 {boundMachineCode}，与本机机器码 {localMachineCode} 不一致。");
            }
        }

        // ── 是否已过期 ──
        var expiresAt = LicenseEvaluator.ResolveExpiry(document);

        if (expiresAt is not null && nowUtc > expiresAt.Value)
        {
            return LicenseImportResult.Fail(
                LicenseImportStatus.Expired,
                $"该授权已于 {BeijingTime.Format(expiresAt.Value)} 过期，无法导入。请索取尚未过期的授权文件。");
        }

        // ── 组装结果 ──
        // 用同一个评估器再跑一遍，保证"导入成功"与"读状态可用"两个判定**必然一致** ——
        // 这里若另写一套判断，就又有机会出现"导入说成功、状态说不可用"的分裂。
        var state = LicenseEvaluator.Evaluate(new LicenseEvaluationInput(
            document,
            newBinding ?? binding,
            SignatureValid: true,
            SignatureError: null,
            LocalMachineCode: localMachineCode,
            NowUtc: nowUtc,
            ClockTolerance: clockTolerance));

        if (!state.IsUsable)
        {
            // 走到这里说明上面各条规则与评估器之间出现了分歧（理论上不该发生）。
            // 此时**不猜测、不放行**，直接把评估器的原话抛回去。
            return LicenseImportResult.Fail(LicenseImportStatus.Expired, state.LastError);
        }

        return new LicenseImportResult(LicenseImportStatus.Ok, "授权导入成功。", document, newBinding, state);
    }
}
