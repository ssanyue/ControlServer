using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlServer.Core.Licensing;
using ControlServer.Core.Services;

namespace ControlServer.LicenseTool.Gui;

/// <summary>
/// 签发授权（本工具唯一的页面）。
///
/// <para>
/// 流程与命令行版一一对应，只是把"命令行参数"换成"表单字段"：
/// 读表单并校验 → 取内置私钥 → 定时刻与联网锚点 → <see cref="LicenseIssuer.Issue"/> → 落盘 → 报告。
/// 全部规则仍由 Core 判定，这里只负责把用户输入翻译成 <see cref="LicenseIssuanceRequest"/>。
/// </para>
///
/// <para>
/// <b>参数校验刻意排在联网取时之前</b>：取时最坏要等十几秒，若把校验放在其后，
/// 一个填错的日期要等网络超时之后才被告知；更糟的是"网络不通 + 表单也填错"时，
/// 用户会先去折腾网络，修好再试，然后才发现真正的错在表单上。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>有效期形态（与授权文件里 et / ed 两个字段一一对应）。</summary>
    private enum ExpiryMode
    {
        Perpetual,
        Date,
        Instant,
    }

    /// <summary>表单变化时重算派生状态；期间抑制重入。</summary>
    private bool _updatingForm;

    // ═══════════════════════════════════════════════════════════
    // 表单交互
    // ═══════════════════════════════════════════════════════════

    private void OnIssueFormChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _updatingForm)
        {
            return;
        }

        UpdateIssueFormState();
    }

    private void OnCustomerChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        TxtOutputHint.Text = $"留空则输出到：{DefaultOutputPath()}";
    }

    /// <summary>
    /// 快捷到期日：把「到期日」设成从今天起算的相应时间，并切到「到期日」模式。
    ///
    /// <para>
    /// 按钮的 <c>Tag</c> 形如 <c>d:3</c>（天）/ <c>m:1</c>（月）/ <c>y:1</c>（年）。
    /// 刻意<b>只作用在「到期日」</b>：若当前停在「精确到期时刻」，点一下会切回「到期日」模式 ——
    /// 一个快捷键只改一个地方，比"看当前模式决定改哪个框"好预测得多。
    /// </para>
    /// </summary>
    private void OnExpiryShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':');

        if (parts.Length != 2
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0)
        {
            return;
        }

        var today = DateTime.Today;

        DpExpireDate.SelectedDate = parts[0] switch
        {
            "d" => today.AddDays(amount),
            "m" => today.AddMonths(amount),
            "y" => today.AddYears(amount),
            _ => today.AddDays(amount),
        };

        // 切模式会触发 Checked → OnIssueFormChanged；模式本来就对时不会触发，所以再显式刷一次。
        RbExpireDate.IsChecked = true;
        UpdateIssueFormState();
    }

    /// <summary>
    /// 根据"授权对象 / 有效期形态"启用或禁用相关输入。
    /// <para>
    /// 用 <c>IsEnabled</c> 而不是 <c>Visibility</c>：控件位置固定，表单不会因为点了单选框而跳动。
    /// </para>
    /// </summary>
    private void UpdateIssueFormState()
    {
        _updatingForm = true;

        try
        {
            var universal = RbUniversal.IsChecked == true;

            // 通用授权在签发时没有机器码，使用截止才有意义；两者互为开关。
            TxtMachineCode.IsEnabled = !universal;
            DpDeadline.IsEnabled = universal;
            TxtDeadlineTime.IsEnabled = universal;

            var mode = CurrentExpiryMode();

            DpExpireDate.IsEnabled = mode == ExpiryMode.Date;
            DpExpireInstant.IsEnabled = mode == ExpiryMode.Instant;
            TxtExpireTime.IsEnabled = mode == ExpiryMode.Instant;

            // 「永久」要求必须绑机器码（Core 的规则）—— 通用授权下直接把这项禁掉，
            // 比让用户点了再报错更清楚。
            RbPerpetual.IsEnabled = !universal;

            if (universal && mode == ExpiryMode.Perpetual)
            {
                RbExpireDate.IsChecked = true;
                DpExpireDate.IsEnabled = true;
            }

            TxtOutputHint.Text = $"留空则输出到：{DefaultOutputPath()}";
        }
        finally
        {
            _updatingForm = false;
        }

        // 放在 finally 之后：预览方法自己也会挡 `_updatingForm`，从里面调等于永远不执行。
        UpdateDeadlinePreview();
    }

    private void OnDeadlineDateChanged(object sender, SelectionChangedEventArgs e) => UpdateDeadlinePreview();

    private void OnDeadlineTimeChanged(object sender, TextChangedEventArgs e) => UpdateDeadlinePreview();

    // ═══════════════════════════════════════════════════════════
    // 使用截止 · 实时预览
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把「使用截止」的日期 + 时间合成一个时刻显示出来，并算出距现在还有多久。
    ///
    /// <para>
    /// <b>为什么要有这一行</b>：默认值只有「今天 23:59:59」，窗口短到可能只剩几个小时。
    /// 日期框和时间框并排放着，看的人得自己在脑子里做减法才知道"还来不来得及" ——
    /// 而这个窗口一旦过去，客户那一天再导入就会被拒（<c>deadlineexceeded</c>），只能重签。
    /// 把余量直接算出来写在脸上，比任何说明文字都省事。
    /// </para>
    ///
    /// <para>
    /// 这里<b>不复制任何业务规则</b>：只判"填的是不是一个时刻、离现在还有多久"。
    /// 真正的约束（必须晚于签发时刻、必须早于授权到期时间）仍由 Core 在签发时判定 ——
    /// 界面上多写一份，早晚会和 Core 的说法打架。
    /// </para>
    /// </summary>
    private void UpdateDeadlinePreview()
    {
        // 初始化期间控件还没建好（Text 属性的 TextChanged 会早于后面的控件实例化），两个条件都要挡。
        if (_loading || _updatingForm || TxtDeadlinePreview is null)
        {
            return;
        }

        // 非通用授权时这个字段不起作用，留着只会让人以为填了有用。
        if (RbUniversal.IsChecked != true)
        {
            TxtDeadlinePreview.Visibility = Visibility.Collapsed;
            return;
        }

        TxtDeadlinePreview.Visibility = Visibility.Visible;

        if (DpDeadline.SelectedDate is not { } date)
        {
            SetDeadlinePreview("还没有选日期 —— 通用授权必须有使用截止。", "C.Danger");
            return;
        }

        if (!TryParseTime(TxtDeadlineTime.Text, out var time))
        {
            SetDeadlinePreview("时间格式不对：应为 HH:mm:ss（24 小时制），例如 23:59:59。", "C.Danger");
            return;
        }

        var deadline = new DateTimeOffset(date.Date + time, BeijingTime.Offset);
        var text = $"使用截止 = {BeijingTime.Format(deadline)}（北京时间）";
        var remaining = deadline - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            SetDeadlinePreview(text + " —— 已经过去，客户此刻导入会被拒，请把日期往后改。", "C.Danger");
            return;
        }

        SetDeadlinePreview(text + $"，距今约 {DescribeRemaining(remaining)}。", "C.Muted");
    }

    private void SetDeadlinePreview(string text, string brushKey)
    {
        TxtDeadlinePreview.Text = text;
        TxtDeadlinePreview.Foreground = (Brush)FindResource(brushKey);
    }

    /// <summary>把剩余时长写成一句人话：超过一天说"天 + 小时"，否则说"小时 + 分"，最后一小时内说分钟。</summary>
    private static string DescribeRemaining(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{remaining.Hours} 小时 {remaining.Minutes} 分";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟";
    }

    private ExpiryMode CurrentExpiryMode()
        => RbPerpetual.IsChecked == true ? ExpiryMode.Perpetual
            : RbExpireInstant.IsChecked == true ? ExpiryMode.Instant
            : ExpiryMode.Date;

    // ═══════════════════════════════════════════════════════════
    // 浏览 / 测试
    // ═══════════════════════════════════════════════════════════

    private void OnPickOutput(object sender, RoutedEventArgs e)
    {
        var defaultPath = DefaultOutputPath();

        var picked = SaveFile(
            "授权文件保存位置",
            "授权文件 (*.lic)|*.lic|所有文件 (*.*)|*.*",
            Path.GetFileName(defaultPath),
            defaultPath);

        if (picked is not null)
        {
            TxtOutput.Text = picked;
        }
    }

    private async void OnTestClock(object sender, RoutedEventArgs e)
    {
        TxtClock.Text = "查询中…";
        BtnIssue.IsEnabled = false;

        try
        {
            var network = await TryQueryNetworkTimeAsync(_issue.Line);

            TxtClock.Text = network is null
                ? "取时失败（见右侧明细）"
                : $"网络时间 {BeijingTime.Format(network.Value)}";
        }
        finally
        {
            BtnIssue.IsEnabled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 签发
    // ═══════════════════════════════════════════════════════════

    private async void OnIssueClick(object sender, RoutedEventArgs e)
        => await RunGuardedAsync(BtnIssue, TxtIssueBusy, _issue, "签发中…", IssueAsync);

    private async Task IssueAsync()
    {
        _issue.Reset();

        // ① 读表单并校验完（不碰网络、不读盘）。
        if (!TryBuildInput(out var input, out var failure))
        {
            _issue.Error(failure);
            _issue.Banner(OutcomeLevel.Error, failure);
            return;
        }

        // ② 内置签发密钥。密钥已编译进本工具，正常情况下这一步不会失败；
        //    万一真的没内嵌，说清"这是构建问题"，而不是让用户去翻密钥文件 —— 那正是要消除的环节。
        if (!LicenseToolKey.IsAvailable)
        {
            _issue.Error(LicenseToolKey.UnavailableMessage(LicenseToolKey.Error));
            _issue.Banner(OutcomeLevel.Error, "签发失败：本工具没有内嵌签发密钥。");
            return;
        }

        var privateKeyPem = LicenseToolKey.Pem;

        _issue.Line("签发密钥：内置厂商密钥");

        // ③ 签发时刻与联网锚点。
        DateTimeOffset anchor;

        if (ChkNtp.IsChecked == true)
        {
            _issue.Line("正在获取网络时间（作为 ivt 联网校对锚点）…");
            var network = await TryQueryNetworkTimeAsync(_issue.Line);

            if (network is null)
            {
                if (ChkOffline.IsChecked != true)
                {
                    _issue.Error("未能取得网络时间，无法写入可信的 ivt 联网锚点。");
                    _issue.Hint("请检查网络与防火墙（NTP 走 UDP 123）后重试；");
                    _issue.Hint("确实无法联网时，勾选「允许用本机时钟」再签，或换一台能联网的机器。");
                    _issue.Banner(OutcomeLevel.Error, "签发失败：取不到网络时间。");
                    return;
                }

                anchor = DateTimeOffset.UtcNow;
                _issue.Warn($"未取得网络时间，已按你的选择使用本机时钟：{BeijingTime.Format(anchor)}（北京时间）");
                _issue.Hint("这样签出来的 ivt 来自本机钟 —— 本机时间若被改过，这个锚点就不可信了。");
            }
            else
            {
                anchor = network.Value;
            }
        }
        else
        {
            anchor = DateTimeOffset.UtcNow;
            _issue.Warn($"已按你的选择不联网，ivt 使用本机时钟：{BeijingTime.Format(anchor)}（北京时间）");
            _issue.Hint("不联网会削弱 ivt 这道防线，仅建议在确定本机时间准确时使用。");
        }

        var localNow = DateTimeOffset.UtcNow;
        var issuedAt = LicenseIssuer.ResolveIssuedAt(localNow, anchor, LicenseEvaluator.DefaultClockTolerance);

        if (issuedAt != localNow)
        {
            _issue.Warn($"本机时钟比网络时间慢约 {(anchor - localNow).TotalSeconds:F0} 秒，已改用网络时间作为签发时刻。");
            _issue.Hint("否则刚签出的授权会因「本机时间早于联网锚点」被拒绝。建议顺手校准系统时间。");
        }

        var request = BuildRequest(input, issuedAt, anchor);

        _issue.Line();
        _issue.Line("正在签发（含密码学自检与服务端导入规则干跑）…");

        // ④ 签发。规则不满足时 Core 抛 LicenseValidationException，文案已经是人话。
        LicenseIssuanceResult result;

        try
        {
            result = LicenseIssuer.Issue(request, privateKeyPem);
        }
        catch (LicenseValidationException ex)
        {
            _issue.Error(ex.Message);
            _issue.Banner(OutcomeLevel.Error, $"签发失败：{ex.Message}");
            return;
        }

        // ⑤ 落盘。覆盖已有文件必须先问一句 —— 相当于命令行版的 --force。
        var outputPath = ResolveOutputPath();

        if (File.Exists(outputPath))
        {
            var answer = MessageBox.Show(
                this,
                $"输出文件已存在：\n{outputPath}\n\n要覆盖它吗？",
                "覆盖确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                _issue.Warn("已取消，输出文件未被覆盖。");
                _issue.Banner(OutcomeLevel.Warn, "已取消：未覆盖已存在的文件。");
                return;
            }
        }

        WriteTextNoBom(outputPath, result.Encoded);

        // ⑥ 报告。
        PrintIssueSummary(outputPath, request, result, input.Universal);

        // 横幅跟着"服务端到底收不收"走：文件是签出来了，但用错了钥匙时它到现场一定被拒，
        // 那是比"签发失败"更坏的结局（失败至少不会把文件发出去）。所以这里用 Error 级。
        _issue.Banner(
            result.MatchesVendorPublicKey ? OutcomeLevel.Ok : OutcomeLevel.Error,
            result.MatchesVendorPublicKey
                ? $"签发成功：{Path.GetFileName(outputPath)}"
                : $"签发完成，但服务端会拒收！签名公钥不是服务端内嵌的那把：{Path.GetFileName(outputPath)}");
    }

    // ═══════════════════════════════════════════════════════════
    // 表单 → 请求
    // ═══════════════════════════════════════════════════════════

    /// <summary>解析并校验后的表单内容（已标准化）。</summary>
    private sealed record IssueInput(
        bool Universal,
        string MachineCode,
        ExpiryMode Mode,
        DateTime ExpireDate,
        DateTimeOffset? ExactExpiry,
        DateTimeOffset? Deadline,
        string Serial,
        string Vendor,
        string Customer);

    private bool TryBuildInput(out IssueInput input, out string failure)
    {
        input = null!;
        failure = string.Empty;

        var universal = RbUniversal.IsChecked == true;

        var serial = TxtSerial.Text.Trim();
        var vendor = TxtVendor.Text.Trim();
        var customer = TxtCustomer.Text.Trim();

        // ── 授权对象 ──
        var machineCode = string.Empty;

        if (universal)
        {
            if (DpDeadline.SelectedDate is not { } deadlineDate || !TryParseTime(TxtDeadlineTime.Text, out var deadlineTime))
            {
                failure = "通用授权必须填写「使用截止」日期与时间（格式 HH:mm:ss）。";
                return false;
            }
        }
        else
        {
            var raw = TxtMachineCode.Text.Trim();

            // 框里**预置文本**是不行的：预置值会成为真实内容，用户不填就提交会得到一份
            // 绑定在示例机器码上的授权。所以这里是空框 + 提示里给格式，未填直接拦下。
            if (raw.Length == 0)
            {
                failure = "请填入客户的机器码（形如 AAAA-BBBB-CCCC-DDDD），或改选「通用授权」。";
                return false;
            }

            if (!LicenseIssuer.TryNormalizeMachineCode(raw, out machineCode))
            {
                failure = $"机器码「{raw}」无法识别：应为 16 位十六进制字符（形如 AAAA-BBBB-CCCC-DDDD，可带可不带横线）。";
                return false;
            }
        }

        // ── 有效期 ──
        var mode = CurrentExpiryMode();
        var expireDate = DateTime.Today;
        DateTimeOffset? exactExpiry = null;

        switch (mode)
        {
            case ExpiryMode.Perpetual when universal:
                failure = "通用授权不能设为永久：它必须有一个明确的期限（到期日或精确到期时刻）。";
                return false;

            case ExpiryMode.Date:
                if (DpExpireDate.SelectedDate is not { } date)
                {
                    failure = "请选择到期日（可用上方「快捷到期日」一键设定）。";
                    return false;
                }

                expireDate = date;
                break;

            case ExpiryMode.Instant:
                if (DpExpireInstant.SelectedDate is not { } instantDate || !TryParseTime(TxtExpireTime.Text, out var instantTime))
                {
                    failure = "精确到期时刻需要一个日期与 HH:mm:ss 的时间。";
                    return false;
                }

                exactExpiry = new DateTimeOffset(instantDate.Date + instantTime, BeijingTime.Offset);
                break;
        }

        DateTimeOffset? deadline = null;

        if (universal && DpDeadline.SelectedDate is { } dd && TryParseTime(TxtDeadlineTime.Text, out var dt))
        {
            deadline = new DateTimeOffset(dd.Date + dt, BeijingTime.Offset);
        }

        input = new IssueInput(
            universal, machineCode, mode, expireDate, exactExpiry, deadline, serial, vendor, customer);

        return true;
    }

    private static LicenseIssuanceRequest BuildRequest(
        IssueInput input,
        DateTimeOffset issuedAt,
        DateTimeOffset anchor) => new()
    {
        // 界面不再区分授权类型（full / trial / temp）：临时授权 = 期限很短的正式授权，
        // 另外两个取值对签发人没有实际区别，一律按正式授权签发。
        Kind = LicenseKind.Full,
        Universal = input.Universal,
        MachineCode = input.MachineCode,
        IssuedAtUtc = issuedAt,
        AnchorUtc = anchor,
        ExactExpiryUtcMs = input.ExactExpiry?.ToUnixTimeMilliseconds() ?? 0,
        ExpiryDate = input.Mode == ExpiryMode.Date
            ? input.ExpireDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : string.Empty,
        // 「自签发起 N 天」已从本界面移除（命令行版仍保留 days 参数），这里恒为 0 = 不使用。
        MaxDays = 0,
        UseDeadlineUtc = input.Deadline,
        Serial = input.Serial,
        Vendor = input.Vendor,
        Customer = input.Customer,
    };

    private static bool TryParseTime(string? text, out TimeSpan value)
        => TimeSpan.TryParseExact(
            (text ?? string.Empty).Trim(),
            new[] { @"hh\:mm\:ss", @"hh\:mm" },
            CultureInfo.InvariantCulture,
            out value);

    // ═══════════════════════════════════════════════════════════
    // 输出路径 / 报告
    // ═══════════════════════════════════════════════════════════

    private string DefaultOutputPath()
    {
        var directory = AppContext.BaseDirectory;
        var customer = TxtCustomer.Text.Trim();
        var serial = TxtSerial.Text.Trim();

        // 优先用客户名（人一眼认得出是谁的授权），否则退到序列号 / 签发日期。
        if (customer.Length > 0)
        {
            return Path.Combine(directory, $"授权_{Sanitize(customer)}.lic");
        }

        if (serial.Length > 0)
        {
            return Path.Combine(directory, $"license_{Sanitize(serial)}.lic");
        }

        return Path.Combine(directory, $"license_{LicenseIssuer.BeijingDate(DateTimeOffset.UtcNow)}.lic");
    }

    private string ResolveOutputPath()
    {
        var typed = TxtOutput.Text.Trim();

        return typed.Length > 0
            ? Path.GetFullPath(typed)
            : Path.GetFullPath(DefaultOutputPath());
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

        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    private void PrintIssueSummary(
        string outputPath,
        LicenseIssuanceRequest request,
        LicenseIssuanceResult result,
        bool universal)
    {
        var document = result.Document;

        _issue.Section("签发完成");
        _issue.Field("授权对象", universal ? "通用（由客户首次导入时自动绑定机器）" : $"{document.Mc}（绑定）");
        _issue.Field("有效期", DescribeExpiry(document));
        _issue.Field("签发时刻", BeijingTime.Format(request.IssuedAtUtc) + "（北京时间）");

        if (document.Ivt > 0)
        {
            _issue.Field(
                "联网锚点",
                $"{BeijingTime.Format(DateTimeOffset.FromUnixTimeMilliseconds(document.Ivt))}（ivt）");
        }

        _issue.Field("序列号", OrNone(document.Sn));
        _issue.Field("厂商标识", OrNone(document.Vi));
        _issue.Field("客户备注", OrNone(document.Hn));

        if (document.Ud > 0)
        {
            _issue.Field(
                "使用截止",
                $"{BeijingTime.Format(DateTimeOffset.FromUnixTimeMilliseconds(document.Ud))}（须在此之前导入）");
        }

        _issue.Field("文件", $"{outputPath}（{new FileInfo(outputPath).Length} 字节）");
        _issue.Field("文件校验", $"SHA256 {Sha256OfFile(outputPath)}");

        _issue.Section("自检");
        _issue.Ok("密码学验签：通过（已按服务端的解码 → 重算载荷 → 验签路径验过）");
        _issue.Ok("服务端导入规则：通过（按 ImportRules 干跑，字段与规则都收得下）");

        // ⚠️ 上面两条**覆盖不到**"签名公钥是不是服务端内嵌的那把"。服务端只认内嵌公钥：
        //    工具内嵌的私钥与服务端内嵌的公钥不是一对时，文件签名自洽、规则也过，
        //    但导入时一律判「签名无效」。这正是本工具上一版最坑人的地方：自检全绿，文件发出去却被拒。
        if (result.MatchesVendorPublicKey)
        {
            _issue.Ok("签名公钥：与服务端内嵌公钥一致 —— 服务端会收下这份文件。");
        }
        else
        {
            _issue.Error("签名公钥与服务端内嵌公钥不一致 —— 这份文件服务端会判「签名无效」，无法导入！");
            _issue.Hint("这说明本工具内嵌的私钥与服务端内嵌的公钥不是一对 —— 通常是换钥时只更新了一边。");
            _issue.Hint("请把新公钥替换进 src/ControlServer.Core/Licensing/VendorPublicKey.cs，");
            _issue.Hint("重编并重新交付服务端，同时重新构建本签发工具。在此之前不要把它发给客户。");
        }

        _issue.Hint("自检只保证「文件本身没毛病、服务端收得下」；");
        _issue.Hint("不保证客户机器上一定可用 —— 那还取决于机器码与客户机器是否一致。");

        _issue.Line();
        _issue.Line("把 .lic 文件发给客户，由管理员在服务端「授权」页导入。");
    }

    private static string DescribeExpiry(LicenseDocument document)
    {
        if (document.Et > 0)
        {
            return BeijingTime.Format(DateTimeOffset.FromUnixTimeMilliseconds(document.Et)) + "（北京时间）";
        }

        if (!string.IsNullOrEmpty(document.Ed)
            && LicenseEvaluator.TryParseBeijingDate(document.Ed, out var date))
        {
            return $"{date:yyyy-MM-dd} 当天 23:59:59 结束（北京时间）";
        }

        if (document.Md > 0)
        {
            return $"自绑定时刻起 {document.Md} 天";
        }

        return "永久（不按日期限制）";
    }

    private static string OrNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "（无）" : value;

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteTextNoBom(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 且不带 BOM：BOM（U+FEFF）在 .NET 里不算空白，会让服务端 Base64 解析失败。
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 按默认服务器列表依次尝试取网络时间；全部失败返回 <c>null</c>，由调用方决定报错还是降级。
    /// <para>复用 Core 的 <see cref="SntpClient"/> 与同一份默认服务器列表，与命令行版口径一致。</para>
    /// </summary>
    private static async Task<DateTimeOffset?> TryQueryNetworkTimeAsync(Action<string> log)
    {
        var client = new SntpClient();

        foreach (var server in TimeSyncSettings.DefaultNtpServers)
        {
            try
            {
                var result = await client.QueryAsync(server, TimeSpan.FromSeconds(3));

                log($"[ok]    已取得网络时间：{BeijingTime.Format(result.ServerUtcTime)}（北京时间）"
                    + $"（来源 {server}，往返 {result.RoundTrip.TotalMilliseconds:F0} ms）");

                return result.ServerUtcTime;
            }
            catch (Exception ex)
            {
                // 单个服务器失败不算失败 —— 多服务器列表的意义就在于此。
                log($"        {server} 取时失败：{ex.Message}");
            }
        }

        return null;
    }
}
