using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 对接码命中的**层级**。
///
/// <para>
/// <b>扫描次序</b>（2026-10-15 起）：
/// <list type="number">
///   <item><b>瓦片级</b>：<see cref="Button"/> 与 <see cref="Combo"/> <b>同级</b> ——
///         两者**按 <c>Order</c> 合并成一个序列**再逐个比对（它们本来就共用同一条全局顺序，
///         界面上也是互相穿插的）。所以这里没有"谁优先"，**顺序就是用户在界面上的顺序**；</item>
///   <item><see cref="SubButton"/>：按「集合顺序 → 集内子按钮顺序」展开；</item>
///   <item><see cref="Action"/>：先按钮内动作，再子按钮内动作。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <b>数值本身不再等于优先级</b>（以前是）。它现在只是"四种命中"的标识 ——
/// 真正决定先后的是上面那条扫描次序。要判"谁先命中"，请看
/// <see cref="PairingMatchService.Match"/>，不要拿枚举值比大小。
/// </para>
/// </summary>
public enum PairingMatchKind
{
    /// <summary>未命中任何对接码。</summary>
    None = 0,

    /// <summary>主按钮级：执行该按钮的**全部动作**（动作各自的 IP / 端口）。</summary>
    Button = 1,

    /// <summary>组合按钮级（2026-10-15，需求⑤）：按成员顺序执行**一串按钮**的动作，段间带延迟。</summary>
    Combo = 2,

    /// <summary>子按钮级：执行该子按钮的全部动作；IP / 端口一律取**所属子按钮集**。</summary>
    SubButton = 3,

    /// <summary>动作级：**只执行命中的那一条**动作。</summary>
    Action = 4,
}

/// <summary>
/// 一次对接码匹配的结果。由 <see cref="PairingMatchService.Match"/> 产出，纯数据。
/// </summary>
public sealed class PairingMatchResult
{
    private static readonly IReadOnlyList<ButtonAction> NoActions = Array.Empty<ButtonAction>();

    private PairingMatchResult(
        PairingMatchKind kind,
        ButtonConfig? button,
        SubButton? subButton,
        SubButtonSet? subButtonSet,
        ButtonAction? action,
        string ownerLabel,
        string? overrideIp,
        int? overridePort,
        string matchedCode,
        string matchedCodeType,
        IReadOnlyList<ButtonAction> actions,
        ComboButton? combo = null,
        IReadOnlyList<ComboSegment>? segments = null,
        IReadOnlyList<string>? skippedButtonIds = null)
    {
        Kind = kind;
        Button = button;
        SubButton = subButton;
        SubButtonSet = subButtonSet;
        Action = action;
        OwnerLabel = ownerLabel ?? string.Empty;
        OverrideIp = overrideIp;
        OverridePort = overridePort;
        MatchedCode = matchedCode ?? string.Empty;
        MatchedCodeType = matchedCodeType ?? string.Empty;
        Actions = actions;
        Combo = combo;
        Segments = segments ?? Array.Empty<ComboSegment>();
        SkippedButtonIds = skippedButtonIds ?? Array.Empty<string>();
    }

    /// <summary>未命中（单例，避免频繁分配）。</summary>
    public static PairingMatchResult None { get; } = new(
        PairingMatchKind.None,
        null, null, null, null,
        string.Empty, null, null, string.Empty, string.Empty,
        NoActions);

    /// <summary>按钮级命中：执行按钮的全部动作，无 IP / 端口覆盖。</summary>
    public static PairingMatchResult ForButton(ButtonConfig button)
    {
        ArgumentNullException.ThrowIfNull(button);

        return new PairingMatchResult(
            PairingMatchKind.Button,
            button, null, null, null,
            button.Label, null, null,
            button.PairingCode, button.PairingCodeType,
            button.Actions ?? NoActions);
    }

    /// <summary>子按钮级命中：执行子按钮的全部动作，IP / 端口取所属集合。</summary>
    public static PairingMatchResult ForSubButton(SubButton subButton, SubButtonSet set)
    {
        ArgumentNullException.ThrowIfNull(subButton);
        ArgumentNullException.ThrowIfNull(set);

        return new PairingMatchResult(
            PairingMatchKind.SubButton,
            null, subButton, set, null,
            subButton.Label, set.TargetIp, set.TargetPort,
            subButton.PairingCode, subButton.PairingCodeType,
            subButton.Actions ?? NoActions);
    }

    /// <summary>
    /// 组合按钮级命中（2026-10-15，需求⑤）：执行**整个组合** —— 按成员顺序逐段执行，
    /// 段间等待各自配置的延迟。
    /// <para>
    /// <b>为什么在这里就把成员解析成段</b>：匹配发生在"配置已经拿到手"的那一刻，
    /// 而执行发生在之后；在这里解析可以保证"匹配到的那份配置"与"执行的那份配置"是同一份快照，
    /// 不会出现"匹配时成员还在、执行时被删了"。解析是纯函数（<see cref="ComboResolver"/>），代价可忽略。
    /// </para>
    /// </summary>
    public static PairingMatchResult ForCombo(ComboButton combo, ComboPlan plan)
    {
        ArgumentNullException.ThrowIfNull(combo);
        ArgumentNullException.ThrowIfNull(plan);

        return new PairingMatchResult(
            PairingMatchKind.Combo,
            null, null, null, null,
            string.IsNullOrWhiteSpace(combo.Label) ? "未命名" : combo.Label,
            null, null,
            combo.PairingCode, combo.PairingCodeType,
            NoActions,
            combo,
            plan.Segments,
            plan.SkippedButtonIds);
    }

    /// <summary>动作级命中：只执行这一条动作；来自子按钮时带上集合的 IP / 端口。</summary>
    public static PairingMatchResult ForAction(
        ButtonAction action,
        string ownerLabel,
        string? overrideIp = null,
        int? overridePort = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        return new PairingMatchResult(
            PairingMatchKind.Action,
            null, null, null, action,
            ownerLabel, overrideIp, overridePort,
            action.PairingCode, action.PairingCodeType,
            new[] { action });
    }

    /// <summary>命中的层级。</summary>
    public PairingMatchKind Kind { get; }

    /// <summary>命中的按钮（仅 <see cref="PairingMatchKind.Button"/>）。</summary>
    public ButtonConfig? Button { get; }

    /// <summary>命中的子按钮（仅 <see cref="PairingMatchKind.SubButton"/>）。</summary>
    public SubButton? SubButton { get; }

    /// <summary>命中子按钮所属的集合（仅 <see cref="PairingMatchKind.SubButton"/>）。</summary>
    public SubButtonSet? SubButtonSet { get; }

    /// <summary>命中的动作（仅 <see cref="PairingMatchKind.Action"/>）。</summary>
    public ButtonAction? Action { get; }

    /// <summary>命中对象的显示名（按钮名 / 子按钮名），直接用作执行结果的 <c>ButtonName</c>。</summary>
    public string OwnerLabel { get; }

    /// <summary>强制覆盖的目标 IP（仅子按钮相关命中非 null）。</summary>
    public string? OverrideIp { get; }

    /// <summary>强制覆盖的目标端口（仅子按钮相关命中非 null）。</summary>
    public int? OverridePort { get; }

    /// <summary>命中的对接码原文。</summary>
    public string MatchedCode { get; }

    /// <summary>命中的对接码类型（String / UTF-8 / Hex）。</summary>
    public string MatchedCodeType { get; }

    /// <summary>本次要执行的动作列表（按钮级 / 子按钮级为全部动作，动作级只有 1 条）。</summary>
    public IReadOnlyList<ButtonAction> Actions { get; }

    /// <summary>命中的组合按钮（仅 <see cref="PairingMatchKind.Combo"/>）。</summary>
    public ComboButton? Combo { get; }

    /// <summary>
    /// 组合要执行的段（仅 <see cref="PairingMatchKind.Combo"/>；已按成员顺序排好、悬空成员已剔除）。
    /// </summary>
    public IReadOnlyList<ComboSegment> Segments { get; }

    /// <summary>组合里被剔除的成员 Id（对应主按钮已不存在）。</summary>
    public IReadOnlyList<string> SkippedButtonIds { get; }

    /// <summary>是否命中。</summary>
    public bool IsMatch => Kind != PairingMatchKind.None;

    /// <summary>命中动作的显示名（为空回退协议名，与旧版一致）。非动作级命中为空串。</summary>
    public string ActionName
        => Action is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(Action.Name) ? Action.Protocol : Action.Name;

    /// <summary>
    /// 写进「接收」日志的说明文字。与旧版 <c>LogService.LogReceive</c> 的 message 参数
    /// <b>逐字对齐</b>（未命中那条在 <see cref="PairingDispatchService"/> 里单独拼）。
    /// </summary>
    public string ReceiveMessage => Kind switch
    {
        PairingMatchKind.Button => $"接收对接码: {MatchedCode}",
        PairingMatchKind.Combo => $"接收组合按钮对接码: {MatchedCode}",
        PairingMatchKind.SubButton => $"接收子按钮对接码: {MatchedCode}",
        PairingMatchKind.Action => $"接收动作对接码: {MatchedCode}",
        _ => string.Empty,
    };

    /// <summary>
    /// 写进「接收」日志的 <c>ButtonName</c>。动作级为「所属对象 / 动作名」（与旧版一致）。
    /// </summary>
    public string LogButtonName
        => Kind == PairingMatchKind.Action ? $"{OwnerLabel} / {ActionName}" : OwnerLabel;

    /// <summary>层级的中文名（日志用）。</summary>
    public string KindName => Kind switch
    {
        PairingMatchKind.Button => "按钮",
        PairingMatchKind.Combo => "组合按钮",
        PairingMatchKind.SubButton => "子按钮",
        PairingMatchKind.Action => "动作",
        _ => "无",
    };
}

/// <summary>
/// 对接码三级匹配（<b>纯函数，无 IO、无状态</b>）。
/// <para>
/// 承担「按钮 → 子按钮 → 动作」的**三段查找**，以及
/// <c>TryMatchAction</c> / <c>IsPairingCodeMatch</c> 两个判定。查找顺序见下方「匹配顺序」一节。
/// 这是 M2 方案 §11.3「匹配」类测试向量的落点，必须能被逐条断言。
/// </para>
/// <para>
/// <b>匹配顺序（= 优先级，逐条与旧版一致）</b>：
/// <list type="number">
///   <item><b>按钮级</b>：按 <c>buttons</c> 的列表顺序，第一个命中者胜出 → 执行该按钮全部动作。</item>
///   <item><b>子按钮级</b>：按「集合顺序 → 集内子按钮顺序」展开后第一个命中者胜出 → 执行该子按钮全部动作，IP / 端口取所属集合。</item>
///   <item><b>动作级</b>：先扫<b>按钮内</b>动作，再扫<b>子按钮内</b>动作；各自按列表顺序取第一个命中 → 只执行那一条。</item>
/// </list>
/// 列表顺序即用户的配置顺序（<c>ControlSettingsNormalizer</c> 已按 <c>Order</c> 排好并重编号）。
/// </para>
/// </summary>
public static class PairingMatchService
{
    /// <summary>
    /// 三级匹配。
    /// </summary>
    /// <param name="text">原始字节按 UTF-8 解码并 Trim 后的文本（<see cref="PairingDatagram.Text"/>）。</param>
    /// <param name="payload">原始字节（<see cref="PairingDatagram.Payload"/>），Hex 比较用。</param>
    /// <param name="buttons">按钮列表（已按 Order 排序）。</param>
    /// <param name="sets">子按钮集列表（已按 Order 排序）。</param>
    /// <param name="combos">
    /// 组合按钮列表（已按 Order 排序，2026-10-15 需求⑤）。
    /// <b>可选参数</b>：老调用点不传 = 没有组合按钮，行为与改之前完全一致。
    /// </param>
    public static PairingMatchResult Match(
        string? text,
        byte[]? payload,
        IReadOnlyList<ButtonConfig>? buttons,
        IReadOnlyList<SubButtonSet>? sets,
        IReadOnlyList<ComboButton>? combos = null)
    {
        var receivedText = text ?? string.Empty;
        var receivedBytes = payload ?? Array.Empty<byte>();

        // ═══════════ ① 瓦片级（主按钮 + 组合按钮，**按 Order 合并扫描**）═══════════
        //
        // 🔥 两者**同级**：它们本来就共用同一条全局 Order（界面顺序就是配置顺序），
        //    所以"谁先被匹配到"应当由那个顺序决定，而不是由"我们先扫了按钮"决定 ——
        //    后者会让用户看到"我把组合按钮拖到前面了，可它的码还是被后面的按钮抢走"。
        foreach (var tile in MergeTilesByOrder(buttons, combos))
        {
            if (tile is ButtonConfig button)
            {
                if (string.IsNullOrEmpty(button.PairingCode)) continue;
                if (!IsPairingCodeMatch(button.PairingCode, button.PairingCodeType, receivedText, receivedBytes)) continue;

                return PairingMatchResult.ForButton(button);
            }

            if (tile is ComboButton combo)
            {
                if (string.IsNullOrEmpty(combo.PairingCode)) continue;
                if (!IsPairingCodeMatch(combo.PairingCode, combo.PairingCodeType, receivedText, receivedBytes)) continue;

                // 顺手把成员解析成段（纯函数，见 ForCombo 的注释）。
                return PairingMatchResult.ForCombo(combo, ComboResolver.Resolve(combo, buttons));
            }
        }

        // ═══════════ ② 子按钮级 ═══════════
        if (sets is not null)
        {
            foreach (var set in sets)
            {
                if (set?.SubButtons is null) continue;

                foreach (var sub in set.SubButtons)
                {
                    if (sub is null) continue;
                    if (string.IsNullOrEmpty(sub.PairingCode)) continue;
                    if (!IsPairingCodeMatch(sub.PairingCode, sub.PairingCodeType, receivedText, receivedBytes)) continue;

                    return PairingMatchResult.ForSubButton(sub, set);
                }
            }
        }

        // ═══════════ ③ 动作级 ═══════════
        // ⚠️ 这里的「文本为空直接不匹配」是**旧版的既有行为**（TryMatchAction 开头即
        //    `if (string.IsNullOrEmpty(code)) return false;`），不是笔误，必须保留：
        //    例如 Hex 对接码 "00" 配上一个只发 0x00 的设备 —— 文本被 Trim 成空串，
        //    于是它在按钮级能命中、在**动作级却不能**。守门用例见 PairingMatchServiceTests。
        if (string.IsNullOrEmpty(receivedText)) return PairingMatchResult.None;

        if (buttons is not null)
        {
            foreach (var button in buttons)
            {
                if (button?.Actions is null) continue;

                var hit = FindAction(button.Actions, receivedText, receivedBytes);
                if (hit is not null)
                {
                    // 按钮内动作：用动作自身的 IP / 端口。
                    return PairingMatchResult.ForAction(hit, button.Label);
                }
            }
        }

        if (sets is not null)
        {
            foreach (var set in sets)
            {
                if (set?.SubButtons is null) continue;

                foreach (var sub in set.SubButtons)
                {
                    if (sub?.Actions is null) continue;

                    var hit = FindAction(sub.Actions, receivedText, receivedBytes);
                    if (hit is not null)
                    {
                        // 子按钮内动作：目标 IP / 端口仍由所属集合统一提供。
                        return PairingMatchResult.ForAction(hit, sub.Label, set.TargetIp, set.TargetPort);
                    }
                }
            }
        }

        return PairingMatchResult.None;
    }

    /// <summary>
    /// 把主按钮与组合按钮按 <c>Order</c> 合并成一个序列（稳定：Order 相同时**按钮在前**）。
    /// <para>
    /// Order 相同时让按钮在前，是为了与清洗器 <c>SortAndReindex</c> 的口径一致 ——
    /// 两边若不一致，就会出现"界面上组合在前、匹配时按钮在前"这种说不清的现象。
    /// </para>
    /// </summary>
    private static IEnumerable<object> MergeTilesByOrder(
        IReadOnlyList<ButtonConfig>? buttons,
        IReadOnlyList<ComboButton>? combos)
    {
        var list = new List<(int Order, int Kind, object Item)>(
            (buttons?.Count ?? 0) + (combos?.Count ?? 0));

        if (buttons is not null)
        {
            foreach (var b in buttons)
            {
                if (b is null) continue;
                list.Add((b.Order, 0, b));
            }
        }

        if (combos is not null)
        {
            foreach (var c in combos)
            {
                if (c is null) continue;
                list.Add((c.Order, 1, c));
            }
        }

        list.Sort(static (a, b) =>
        {
            var cmp = a.Order.CompareTo(b.Order);
            return cmp != 0 ? cmp : a.Kind.CompareTo(b.Kind);
        });

        foreach (var item in list) yield return item.Item;
    }

    /// <summary>
    /// 按「对接码数据类型」决定比较方式 —— 与旧版 <c>IsPairingCodeMatch</c> 逐行等价：
    /// <list type="bullet">
    ///   <item><b>Hex</b>：对接码去空白转大写 与 <b>原始字节</b>的大写十六进制串做<b>Ordinal 精确比较</b>；</item>
    ///   <item><b>其它（String / UTF-8 / 未知）</b>：对接码与解码文本做 <b>OrdinalIgnoreCase</b> 比较。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 与旧版唯一的差别：旧版判定 Hex 用的是 <c>pairingCodeType == "Hex"</c>（Ordinal <b>区分</b>大小写），
    /// 这里用 <see cref="DataEncoder.IsHex"/>（OrdinalIgnoreCase）。属于**更宽松的超集**，
    /// 只在对接码类型被手工写成 <c>"hex"</c> 的畸形配置上产生差异（旧版那种配置不匹配，服务版匹配）。
    /// </remarks>
    public static bool IsPairingCodeMatch(
        string? pairingCode,
        string? pairingCodeType,
        string? text,
        byte[]? payload)
    {
        if (string.IsNullOrEmpty(pairingCode)) return false;

        if (DataEncoder.IsHex(pairingCodeType))
        {
            return string.Equals(
                DataEncoder.NormalizeHex(pairingCode),
                DataEncoder.ToHexString(payload ?? Array.Empty<byte>()),
                StringComparison.Ordinal);
        }

        return string.Equals(pairingCode, text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在一组动作里找第一个「有对接码且命中」的动作。找不到返回 null。</summary>
    private static ButtonAction? FindAction(
        IReadOnlyList<ButtonAction> actions,
        string receivedText,
        byte[] receivedBytes)
    {
        foreach (var action in actions)
        {
            if (action is null) continue;
            if (string.IsNullOrEmpty(action.PairingCode)) continue;
            if (!IsPairingCodeMatch(action.PairingCode, action.PairingCodeType, receivedText, receivedBytes)) continue;

            return action;
        }

        return null;
    }
}
