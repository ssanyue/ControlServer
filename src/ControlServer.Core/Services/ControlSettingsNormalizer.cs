using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 配置兼容清洗器（**纯函数**：输入 → 就地清洗并返回说明，无 IO、无依赖）。
/// <para>
/// 处理顺序与判定条件照旧版 <c>ConfigService.LoadFromDisk()</c> 复刻 ——
/// 这是"行为等价"的核心资产之一，必须能被测试向量直接验证。
/// </para>
/// <para>
/// 处理内容：
/// <list type="number">
///   <item>所有可空字段 <c>??=</c> 兜底（旧配置 / 损坏文件兼容）；</item>
///   <item>三项旧格式迁移：<c>TimerTime→TimerTimes</c>、<c>Time→Times</c>、<c>Months+Days→MonthDays</c>；</item>
///   <item>子按钮动作强制去除 WoL（降级为 UDP）；</item>
///   <item>端口越界校正（ListenPort、子按钮集 TargetPort）；</item>
///   <item>按钮 + 子按钮集**合并排序**并重编号 0..N-1（Order 相同按钮优先）。</item>
/// </list>
/// </para>
/// </summary>
public static class ControlSettingsNormalizer
{
    /// <summary>默认监听端口（旧版规则）。</summary>
    public const int DefaultListenPort = 9999;

    /// <summary>默认动作 / 集合目标端口（旧版规则）。</summary>
    public const int DefaultTargetPort = 8080;

    /// <summary>默认目标 IP（旧版规则）。</summary>
    public const string DefaultTargetIp = "192.168.1.100";

    /// <summary>默认动作对接码类型。</summary>
    public const string DefaultActionPairingCodeType = PairingCodeTypes.String;

    /// <summary>子按钮对接码类型默认值（与按钮不同，旧版即如此）。</summary>
    public const string DefaultSubButtonPairingCodeType = PairingCodeTypes.Utf8;

    /// <summary>
    /// 定时时间点默认值。
    /// <para>
    /// ⚠️ <b>必须是 <c>HH:mm:ss</c>，不能是 <c>"08:00"</c></b>：定时器按
    /// <c>HH:mm:ss</c> 做精确字符串匹配，少一段就永远匹配不上 → 默认配置的定时器**静默不触发**。
    /// </para>
    /// </summary>
    private const string DefaultTime = "08:00:00";

    /// <summary>
    /// 就地清洗一份配置。返回本次实际执行的**迁移动作说明**（供导入结果告知用户，不做静默吞掉）。
    /// </summary>
    public static IReadOnlyList<string> Normalize(ControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var notes = new List<string>();

        settings.Buttons ??= new();
        settings.SubButtonSets ??= new();
        settings.ComboButtons ??= new();
        settings.BackgroundImagePath ??= "";
        settings.AppName ??= "中控";
        settings.DefaultButtonImage ??= "";

        // 反序列化可能产出 null 元素（如 JSON 里写了 [null]）—— 旧版会直接崩，这里剔除（加固，不改变合法配置行为）。
        settings.Buttons.RemoveAll(static b => b is null);
        settings.SubButtonSets.RemoveAll(static s => s is null);
        settings.ComboButtons.RemoveAll(static c => c is null);

        foreach (var btn in settings.Buttons)
        {
            NormalizeButton(btn, notes);
        }

        foreach (var set in settings.SubButtonSets)
        {
            NormalizeSubButtonSet(set, notes);
        }

        // ⚠️ 组合按钮**必须排在主按钮之后**清洗：它的成员是按 Id 引用主按钮的，
        //    先洗主按钮才能保证"成员指向的按钮确实存在"这条判断是干净的。
        foreach (var combo in settings.ComboButtons)
        {
            NormalizeComboButton(combo, notes, settings.Buttons);
        }

        // 端口越界校正（只校正旧版校正过的两处，保证等价）。
        if (settings.ListenPort <= 0 || settings.ListenPort > 65535)
        {
            notes.Add($"监听端口 {settings.ListenPort} 越界，已回退为 {DefaultListenPort}");
            settings.ListenPort = DefaultListenPort;
        }

        // 排序归一化放在最后：上面任何一步都不改变 Order，这里统一重编号。
        SortAndReindex(settings, notes);

        return notes;
    }

    private static void NormalizeButton(ButtonConfig btn, List<string> notes)
    {
        btn.Actions ??= new();
        btn.MonthlyPlans ??= new();
        btn.TimerDays ??= new() { 1, 2, 3, 4, 5 };
        btn.Label ??= "按钮";
        btn.PairingCode ??= "";
        btn.PairingCodeType ??= PairingCodeTypes.String;
        btn.ButtonColor ??= "#512DA8";
        btn.TextColor ??= "#FFFFFF";
        btn.ButtonImage ??= "";
        btn.TimerMode ??= TimerModes.Daily;
        btn.TimerTimes ??= new();

        // 旧字段 TimerTime 只在**存在**时才参与迁移 —— 模型默认值刻意留空，
        // 使得"JSON 里确实写了 timerTime"与"字段缺失"可区分（否则会产生假的迁移说明）。
        // 注意：NormalizeTimeString 同时承担"解析"与"校验旧值合法性"两件事。
        var legacyTimerTime = NormalizeTimeString(btn.TimerTime);

        if (btn.TimerTimes.Count == 0)
        {
            if (legacyTimerTime is not null)
            {
                // 迁移 ①：旧配置只有 TimerTime，迁移到 TimerTimes。
                btn.TimerTimes.Add(legacyTimerTime);
                notes.Add($"按钮「{btn.Label}」：旧字段 TimerTime=\"{btn.TimerTime}\" 已迁移为 TimerTimes（{legacyTimerTime}）");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(btn.TimerTime))
                {
                    notes.Add($"按钮「{btn.Label}」：旧字段 TimerTime=\"{btn.TimerTime}\" 无法识别（应为 HH:mm 或 HH:mm:ss），已改用默认 {DefaultTime}");
                }

                btn.TimerTimes.Add(DefaultTime);
            }
        }

        // 兜底补秒：走上一步的默认值已合规，但来自 JSON 或旧配置的值可能还是 "HH:mm"。
        NormalizeTimeList(btn.TimerTimes, notes, $"按钮「{btn.Label}」");

        // 旧字段回填为当前首个时间点，与旧版保存时的写法一致（旧版：TimerTime = TimerTimes.First()）。
        // 这样旧版读回服务版写出的配置时，旧字段语义仍然自洽。
        btn.TimerTime = btn.TimerTimes.Count > 0 ? btn.TimerTimes[0] : DefaultTime;

        btn.Actions.RemoveAll(static a => a is null);
        foreach (var act in btn.Actions)
        {
            NormalizeAction(act, notes, allowWol: true, ownerLabel: $"按钮「{btn.Label}」");
        }

        btn.MonthlyPlans.RemoveAll(static p => p is null);
        foreach (var plan in btn.MonthlyPlans)
        {
            NormalizeMonthlyPlan(plan, btn, notes);
        }
    }

    /// <summary>
    /// 清洗一个组合按钮（2026-10-15，需求⑤）。<b>必须传"当前所有主按钮"</b>：
    /// 成员是引用，指向不存在的按钮必须剔除（并且**说出来**，不做静默吞掉）。
    /// </summary>
    private static void NormalizeComboButton(
        ComboButton combo,
        List<string> notes,
        IReadOnlyList<ButtonConfig> allButtons)
    {
        combo.Label ??= "组合按钮";
        combo.PairingCode ??= "";
        combo.PairingCodeType ??= PairingCodeTypes.String;
        combo.ButtonColor ??= "#512DA8";
        combo.TextColor ??= "#FFFFFF";
        combo.ButtonImage ??= "";
        combo.Steps ??= new();

        combo.Steps.RemoveAll(static s => s is null);

        var known = new HashSet<string>(
            allButtons.Where(static b => b is not null).Select(static b => b.Id),
            StringComparer.Ordinal);

        var kept = new List<ComboButtonStep>(combo.Steps.Count);
        var dropped = 0;

        foreach (var step in combo.Steps)
        {
            var id = (step.ButtonId ?? string.Empty).Trim();

            if (id.Length == 0)
            {
                dropped++;
                continue;
            }

            // 悬空成员：那个主按钮已经被删了（或被排除在外）。**剔除**而不是留着一个空位 ——
            // 留着会让"组合里有几步"变成一个骗人的数字。
            if (!known.Contains(id))
            {
                dropped++;
                continue;
            }

            step.ButtonId = id;

            if (step.DelayMs < 0)
            {
                notes.Add($"组合按钮「{combo.Label}」：段间延迟 {step.DelayMs}ms 非法（负数），已按 0 处理");
                step.DelayMs = 0;
            }
            else if (step.DelayMs > ComboButtonStep.MaxDelayMs)
            {
                notes.Add(
                    $"组合按钮「{combo.Label}」：段间延迟 {step.DelayMs}ms 超出上限，已截到 {ComboButtonStep.MaxDelayMs}ms"
                    + "（要等更久请用定时器；组合是在「有人在场」的链路上跑的）");
                step.DelayMs = ComboButtonStep.MaxDelayMs;
            }

            // 把"引用同一个按钮两次"也保留下来：那是有意义的行为（连按两次）。
            kept.Add(step);
        }

        if (dropped > 0)
        {
            notes.Add($"组合按钮「{combo.Label}」：{dropped} 个成员指向的主按钮已不存在，已剔除（组合现在有 {kept.Count} 步）");
        }

        // 段间延迟的定义是"本步之后、下一步之前" ⇒ **最后一步的延迟永远不会生效**。
        // 归零而不是留着：留一个"填了 500 却从不生效"的数字，是下一轮排障的陷阱。
        if (kept.Count > 0) kept[^1].DelayMs = 0;

        combo.Steps = kept;
    }

    private static void NormalizeSubButtonSet(SubButtonSet set, List<string> notes)
    {
        set.SubButtons ??= new();
        set.Label ??= "子按钮集";
        set.ButtonColor ??= "#00695C";
        set.TextColor ??= "#FFFFFF";
        set.ButtonImage ??= "";
        set.TargetIp ??= DefaultTargetIp;

        if (set.TargetPort <= 0 || set.TargetPort > 65535)
        {
            notes.Add($"子按钮集「{set.Label}」目标端口 {set.TargetPort} 越界，已回退为 {DefaultTargetPort}");
            set.TargetPort = DefaultTargetPort;
        }

        set.SubButtons.RemoveAll(static s => s is null);
        foreach (var sub in set.SubButtons)
        {
            sub.Label ??= "子按钮";
            sub.PairingCode ??= "";
            // 旧版此处未做 ??=（依赖类默认值）。这里补上，属于**加固**：只影响 "pairingCodeType": null 的畸形配置。
            sub.PairingCodeType ??= DefaultSubButtonPairingCodeType;
            sub.ButtonColor ??= "#00897B";
            sub.TextColor ??= "#FFFFFF";
            sub.ButtonImage ??= "";
            sub.Actions ??= new();

            sub.Actions.RemoveAll(static a => a is null);
            foreach (var act in sub.Actions)
            {
                // 子按钮动作不允许 WoL：旧版在**载入时**强制降级为 UDP。
                NormalizeAction(act, notes, allowWol: false, ownerLabel: $"子按钮「{sub.Label}」");
            }
        }
    }

    private static void NormalizeAction(ButtonAction act, List<string> notes, bool allowWol, string ownerLabel)
    {
        act.TargetIp ??= DefaultTargetIp;
        act.Protocol ??= TransportProtocols.Udp;
        act.DataType ??= DataTypes.String;
        act.DataContent ??= "";

        // ── 别名合并：UTF-8 → String ──
        // 两者编码结果**逐字节相同**（DataEncoder.Encode 的两个分支都走 Encoding.UTF8），
        // 历史上并存只是旧版遗留的语义别名。自 2026-10-08 起统一归一为 String，
        // 这样界面、导出、指令库、后端判定只认一个取值 —— 否则前端下拉删掉 UTF-8 后，
        // 旧配置里的 UTF-8 会让下拉显示空白（同 WoL 那轮的教训：只隐藏不归位 = 更隐蔽的误导）。
        // 值真变了才记说明；改完即为 String，二次清洗不再产生输出（保持幂等）。
        if (DataEncoder.IsUtf8(act.DataType))
        {
            act.DataType = DataTypes.String;
            notes.Add($"{ownerLabel}：动作「{act.Name}」的数据类型 UTF-8 已合并为 String（两者编码结果完全相同）");
        }
        act.Name ??= "";
        act.PairingCode ??= "";
        act.PairingCodeType ??= DefaultActionPairingCodeType;

        if (!allowWol && string.Equals(act.Protocol, TransportProtocols.Wol, StringComparison.Ordinal))
        {
            act.Protocol = TransportProtocols.Udp;
            notes.Add($"{ownerLabel}：动作「{act.Name}」的 WoL 已强制降级为 UDP（子按钮不支持网络唤醒）");
        }
    }

    private static void NormalizeMonthlyPlan(MonthlyPlan plan, ButtonConfig btn, List<string> notes)
    {
        plan.Name ??= "计划";
        plan.Times ??= new();
        plan.Months ??= new();
        plan.Days ??= new();
        plan.MonthDays ??= new();

        var legacyPlanTime = NormalizeTimeString(plan.Time);

        if (plan.Times.Count == 0)
        {
            if (legacyPlanTime is not null)
            {
                // 迁移 ②：旧配置只有 Time，迁移到 Times。
                plan.Times.Add(legacyPlanTime);
                notes.Add($"按钮「{btn.Label}」计划「{plan.Name}」：旧字段 Time=\"{plan.Time}\" 已迁移为 Times（{legacyPlanTime}）");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(plan.Time))
                {
                    notes.Add($"按钮「{btn.Label}」计划「{plan.Name}」：旧字段 Time=\"{plan.Time}\" 无法识别（应为 HH:mm 或 HH:mm:ss），已改用默认 {DefaultTime}");
                }

                plan.Times.Add(DefaultTime);
            }
        }

        NormalizeTimeList(plan.Times, notes, $"按钮「{btn.Label}」计划「{plan.Name}」");
        plan.Time = plan.Times.Count > 0 ? plan.Times[0] : DefaultTime;

        // 迁移 ③：旧配置用 Months + Days 交叉匹配，展开为 MonthDays（每月独立日期）。
        if (plan.MonthDays.Count == 0 && plan.Months.Count > 0 && plan.Days.Count > 0)
        {
            foreach (var m in plan.Months)
            {
                plan.MonthDays.Add(new MonthDaySchedule
                {
                    Month = m,
                    Days = plan.Days.ToList(),
                });
            }

            notes.Add(
                $"按钮「{btn.Label}」计划「{plan.Name}」：Months=[{string.Join(',', plan.Months)}] × Days=[{string.Join(',', plan.Days)}] 已展开为 MonthDays（{plan.MonthDays.Count} 条）");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // 时间点格式归一化
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 把时间点规范化为 <c>HH:mm:ss</c>；无法识别时返回 <c>null</c>。
    /// <para>
    /// 接受 <c>H:m</c> / <c>HH:mm</c> / <c>HH:mm:ss</c>（数字部分允许前导/尾随空白），
    /// 时 0-23、分 0-59、秒 0-59。
    /// </para>
    /// <para>
    /// ⚠️ <b>为什么"补秒"是必须的而不是美化</b>：定时器用
    /// <c>times.Contains(now.ToString("HH:mm:ss"))</c> 做**精确字符串匹配**，
    /// 所以配了 <c>"08:00"</c> 就等于配了一个**永远不可能等于当前时刻**的值 ——
    /// 定时器静默不触发、日志里什么都没有、用户只能看到"到点了没反应"。
    /// 旧版靠编辑页保存时 <c>TimeInput</c> 往返顺带补秒，配置侧从未补过；
    /// 服务版在这里统一兜住（见 M2 方案 §10.7 第 2 个实例）。
    /// </para>
    /// </summary>
    public static string? NormalizeTimeString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out var hour) || hour is < 0 or > 23)
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var minute) || minute is < 0 or > 59)
        {
            return null;
        }

        var second = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out second) || second is < 0 or > 59))
        {
            return null;
        }

        return $"{hour:D2}:{minute:D2}:{second:D2}";
    }

    /// <summary>
    /// 就地归一化一组时间点：逐个补秒、剔除无法识别的、去重（保留首次出现的顺序）。
    /// 每一次实际改动都会写进 <paramref name="notes"/>，**不做静默修改**。
    /// </summary>
    /// <param name="list">待处理的时间点列表（就地修改）。</param>
    /// <param name="notes">说明收集器，可为 <c>null</c>。</param>
    /// <param name="owner">用于说明文案的所有者描述，如 <c>按钮「开灯」</c>。</param>
    public static void NormalizeTimeList(List<string> list, List<string>? notes, string owner)
    {
        ArgumentNullException.ThrowIfNull(list);

        var kept = new List<string>(list.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in list)
        {
            var normalized = NormalizeTimeString(raw);

            if (normalized is null)
            {
                notes?.Add($"{owner}：时间点 \"{raw}\" 无法识别（应为 HH:mm 或 HH:mm:ss，时分秒需在有效范围内），已忽略");
                continue;
            }

            if (!seen.Add(normalized))
            {
                notes?.Add($"{owner}：时间点 \"{raw}\" 与已有时间点重复（归一化后均为 {normalized}），已去重");
                continue;
            }

            if (!string.Equals(raw, normalized, StringComparison.Ordinal))
            {
                notes?.Add($"{owner}：时间点 \"{raw}\" 已补全为 {normalized}（原格式无法被定时器匹配）");
            }

            kept.Add(normalized);
        }

        list.Clear();
        list.AddRange(kept);
    }

    // 三类瓦片在同一 Order 空间里的"同级次序"（数值越小越靠前）。
    // 用常量而不是裸 0/1/2：读代码的人不该去猜"1 是什么意思"。
    private const int KindButton = 0;
    private const int KindCombo = 1;
    private const int KindSet = 2;

    /// <summary>
    /// 排序归一化：按钮与子按钮集视为**同级**，按 <c>Order</c> 合并排序后重新编号 <c>0..N-1</c>。
    /// <para>
    /// 同级（Order 相同，如旧配置全为 0）时**按钮优先于子按钮集**，保持「先按钮后集合」的原有布局。
    /// </para>
    /// </summary>
    public static void SortAndReindex(ControlSettings settings, List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Buttons ??= new();
        settings.SubButtonSets ??= new();
        settings.ComboButtons ??= new();

        // 三类瓦片**共用同一条 Order 空间**（2026-10-15 起加入组合按钮）：
        // 界面上"谁在前"只有一个真相源，就是这里排出来的顺序。
        var combined = new List<(int Order, int Kind, object Item)>(
            settings.Buttons.Count + settings.SubButtonSets.Count + settings.ComboButtons.Count);

        foreach (var b in settings.Buttons)
        {
            combined.Add((b.Order, KindButton, b));
        }

        foreach (var c in settings.ComboButtons)
        {
            combined.Add((c.Order, KindCombo, c));
        }

        foreach (var s in settings.SubButtonSets)
        {
            combined.Add((s.Order, KindSet, s));
        }

        // 稳定排序：Order 升序；Order 相同时按 **按钮 < 组合按钮 < 子按钮集** 排，
        // 保持旧配置（全是 Order=0）"先按钮、后集合"的原布局不变。
        combined.Sort(static (a, b) =>
        {
            var c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });

        var newButtons = new List<ButtonConfig>(settings.Buttons.Count);
        var newCombos = new List<ComboButton>(settings.ComboButtons.Count);
        var newSets = new List<SubButtonSet>(settings.SubButtonSets.Count);
        var order = 0;
        var changed = false;

        foreach (var item in combined)
        {
            switch (item.Kind)
            {
                case KindCombo:
                {
                    var c = (ComboButton)item.Item;
                    if (c.Order != order) changed = true;
                    c.Order = order++;
                    newCombos.Add(c);
                    break;
                }

                case KindSet:
                {
                    var s = (SubButtonSet)item.Item;
                    if (s.Order != order) changed = true;
                    s.Order = order++;
                    newSets.Add(s);
                    break;
                }

                default:
                {
                    var b = (ButtonConfig)item.Item;
                    if (b.Order != order) changed = true;
                    b.Order = order++;
                    newButtons.Add(b);
                    break;
                }
            }
        }

        // 顺序没变化时不追加说明 —— 保证「清洗是幂等的、二次清洗无输出」。
        if (changed)
        {
            notes?.Add(
                $"排序归一化：按钮 {newButtons.Count} 个 + 组合按钮 {newCombos.Count} 个 + 子按钮集 {newSets.Count} 个，"
                + $"重编号 0..{Math.Max(0, order - 1)}");
        }

        settings.Buttons = newButtons;
        settings.ComboButtons = newCombos;
        settings.SubButtonSets = newSets;
    }
}
