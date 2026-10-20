using System.Text;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 对接码<b>三级匹配</b>的行为等价性测试。
/// 三段查找（按钮 → 子按钮 → 动作）与两个判定函数：
/// <c>TryMatchAction</c> / <c>IsPairingCodeMatch</c>。
/// 覆盖 M2 方案 §11.3 的「匹配-按钮级 / 匹配-子按钮级 / 匹配-动作级 / 优先级 / Hex 匹配 / 大小写」向量。
/// </summary>
public sealed class PairingMatchServiceTests
{
    // ═══════════════════════════════════════════════════════════
    // ① 按钮级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Button_level_match_returns_all_of_its_actions()
    {
        var button = Btn("客厅灯", "LIGHT_ON", actions: Acts(Act("开灯"), Act("留痕")));
        var sets = new List<SubButtonSet>();

        var match = PairingMatchService.Match("LIGHT_ON", Bytes("LIGHT_ON"), new[] { button }, sets);

        match.Kind.Should().Be(PairingMatchKind.Button);
        match.OwnerLabel.Should().Be("客厅灯");
        match.Actions.Should().HaveCount(2, "按钮级命中要执行该按钮的全部动作");
        match.OverrideIp.Should().BeNull("按钮级覆盖 IP 应由动作自身决定");
        match.OverridePort.Should().BeNull();
        match.ReceiveMessage.Should().Be("接收对接码: LIGHT_ON");
        match.LogButtonName.Should().Be("客厅灯");
    }

    [Fact]
    public void Button_level_match_is_case_insensitive_for_text_codes()
    {
        // 旧版向量：对接码 "LIGHT_ON" 收到 "light_on" → 命中（OrdinalIgnoreCase）。
        var button = Btn("灯", "LIGHT_ON");

        var match = PairingMatchService.Match("light_on", Bytes("light_on"), new[] { button }, Empty());

        match.Kind.Should().Be(PairingMatchKind.Button);
    }

    [Fact]
    public void Button_level_match_ignores_surrounding_whitespace_because_text_is_already_trimmed()
    {
        // 旧版在收包后 Trim()，因此"包裹了空白的对接码"能命中；Trim 之前不行。
        var button = Btn("门", "OPEN");

        PairingMatchService.Match("  OPEN  ".Trim(), Bytes("  OPEN  "), new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.Button);
    }

    [Fact]
    public void Button_with_empty_pairing_code_never_matches()
    {
        var button = Btn("无码按钮", "");

        PairingMatchService.Match("", Array.Empty<byte>(), new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.None, "未配置对接码的按钮不参与匹配（旧版同样以 IsNullOrEmpty 跳过）");
    }

    [Fact]
    public void First_matching_button_in_list_order_wins()
    {
        var first = Btn("先", "SAME");
        var second = Btn("后", "SAME");

        var match = PairingMatchService.Match("SAME", Bytes("SAME"), new[] { first, second }, Empty());

        match.OwnerLabel.Should().Be("先", "列表顺序即配置顺序，第一个命中者胜出");
    }

    // ═══════════════════════════════════════════════════════════
    // ② 子按钮级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SubButton_level_match_carries_the_owning_sets_ip_and_port()
    {
        var set = new SubButtonSet
        {
            Label = "机房",
            TargetIp = "10.0.0.9",
            TargetPort = 5001,
            SubButtons = { Sub("空调", "AC_ON") },
        };

        var match = PairingMatchService.Match("AC_ON", Bytes("AC_ON"), Array.Empty<ButtonConfig>(), new[] { set });

        match.Kind.Should().Be(PairingMatchKind.SubButton);
        match.OwnerLabel.Should().Be("空调");
        match.SubButtonSet.Should().BeSameAs(set);
        match.OverrideIp.Should().Be("10.0.0.9");
        match.OverridePort.Should().Be(5001);
        match.ReceiveMessage.Should().Be("接收子按钮对接码: AC_ON");
        match.LogButtonName.Should().Be("空调");
    }

    [Fact]
    public void SubButton_level_match_returns_all_of_its_actions()
    {
        var set = new SubButtonSet
        {
            Label = "集",
            SubButtons = { Sub("子", "S1", actions: Acts(Act("一"), Act("二"))) },
        };

        var match = PairingMatchService.Match("S1", Bytes("S1"), Array.Empty<ButtonConfig>(), new[] { set });

        match.Actions.Should().HaveCount(2);
    }

    [Fact]
    public void SubButton_default_pairing_code_type_is_utf8_and_still_matches_text()
    {
        // 子按钮的 PairingCodeType 默认是 UTF-8（与按钮的 String 不同）—— 非 Hex 一律走文本比较。
        var sub = Sub("子", "TEMP");
        sub.PairingCodeType.Should().Be(PairingCodeTypes.Utf8, "守门：子按钮默认类型是 UTF-8");

        var set = new SubButtonSet { Label = "集", SubButtons = { sub } };

        PairingMatchService.Match("TEMP", Bytes("TEMP"), Array.Empty<ButtonConfig>(), new[] { set }).Kind
            .Should().Be(PairingMatchKind.SubButton);
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 动作级
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Action_level_match_inside_a_button_returns_exactly_one_action_without_override()
    {
        var button = Btn("混合", "BUTTON_CODE", actions: Acts(Act("带码", "ONLY_ME"), Act("不带码")));

        var match = PairingMatchService.Match("ONLY_ME", Bytes("ONLY_ME"), new[] { button }, Empty());

        match.Kind.Should().Be(PairingMatchKind.Action);
        match.Actions.Should().ContainSingle("动作级命中只执行命中的那一条");
        match.Actions[0].Name.Should().Be("带码");
        match.OwnerLabel.Should().Be("混合");
        match.OverrideIp.Should().BeNull("按钮内动作使用动作自身 IP/端口");
        match.OverridePort.Should().BeNull();
        match.ReceiveMessage.Should().Be("接收动作对接码: ONLY_ME");
        match.LogButtonName.Should().Be("混合 / 带码", "旧版动作级日志的 ButtonName 是「所属 / 动作名」");
    }

    [Fact]
    public void Action_level_match_inside_a_subbutton_inherits_the_sets_ip_and_port()
    {
        var set = new SubButtonSet
        {
            Label = "集",
            TargetIp = "10.1.1.1",
            TargetPort = 6001,
            SubButtons = { Sub("子", "SUB_CODE", actions: Act("子动作", "SUB_ACTION")) },
        };

        var match = PairingMatchService.Match("SUB_ACTION", Bytes("SUB_ACTION"), Array.Empty<ButtonConfig>(), new[] { set });

        match.Kind.Should().Be(PairingMatchKind.Action);
        match.Actions.Should().ContainSingle();
        match.OverrideIp.Should().Be("10.1.1.1", "子按钮内的动作，IP 仍由所属集合统一提供");
        match.OverridePort.Should().Be(6001);
        match.LogButtonName.Should().Be("子 / 子动作");
    }

    [Fact]
    public void Action_name_falls_back_to_protocol_when_blank()
    {
        var action = Act("", "CODE");
        var button = Btn("按钮", "", actions: action);

        var match = PairingMatchService.Match("CODE", Bytes("CODE"), new[] { button }, Empty());

        match.ActionName.Should().Be(TransportProtocols.Udp, "动作名为空回退协议名（与旧版一致）");
    }

    [Fact]
    public void Button_actions_are_searched_before_subbutton_actions()
    {
        var button = Btn("按钮", "", actions: Act("按钮内动作", "SHARED"));
        var set = new SubButtonSet { Label = "集", SubButtons = { Sub("子", "", actions: Act("子按钮内动作", "SHARED")) } };

        var match = PairingMatchService.Match("SHARED", Bytes("SHARED"), new[] { button }, new[] { set });

        match.OwnerLabel.Should().Be("按钮", "动作级匹配先扫按钮内动作，再扫子按钮内动作");
        match.Action!.Name.Should().Be("按钮内动作");
    }

    // ═══════════════════════════════════════════════════════════
    // 优先级（§11.3「优先级」向量）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Button_level_beats_action_level_for_the_same_code()
    {
        // §11.3 原文：同一码同时命中"动作级"与"按钮级" → 按钮级胜出。
        var byButton = Btn("按按钮级命中", "DUP", actions: Act("甲"));
        var byAction = Btn("按动作级命中", "", actions: Act("乙", "DUP"));

        var match = PairingMatchService.Match("DUP", Bytes("DUP"), new[] { byButton, byAction }, Empty());

        match.Kind.Should().Be(PairingMatchKind.Button);
        match.OwnerLabel.Should().Be("按按钮级命中");
        match.Actions.Should().ContainSingle().Which.Name.Should().Be("甲");
    }

    [Fact]
    public void Button_level_beats_subbutton_level_for_the_same_code()
    {
        var button = Btn("按钮", "DUP");
        var set = new SubButtonSet
        {
            Label = "集",
            TargetIp = "10.0.0.1",
            TargetPort = 7001,
            SubButtons = { Sub("子", "DUP") },
        };

        var match = PairingMatchService.Match("DUP", Bytes("DUP"), new[] { button }, new[] { set });

        match.Kind.Should().Be(PairingMatchKind.Button);
        match.OverrideIp.Should().BeNull();
    }

    [Fact]
    public void SubButton_level_beats_action_level_for_the_same_code()
    {
        // 子按钮级命中后**不再**去找动作级（旧版是 if/else 链）。
        var sub = Sub("子", "DUP", actions: Act("子动作", "DUP"));
        var set = new SubButtonSet { Label = "集", TargetIp = "10.0.0.1", TargetPort = 7001, SubButtons = { sub } };

        var match = PairingMatchService.Match("DUP", Bytes("DUP"), Array.Empty<ButtonConfig>(), new[] { set });

        match.Kind.Should().Be(PairingMatchKind.SubButton);
        match.OverrideIp.Should().Be("10.0.0.1");
    }

    // ═══════════════════════════════════════════════════════════
    // Hex / 编码相关
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Hex_pairing_code_matches_on_raw_bytes_not_on_decoded_text()
    {
        // §11.3 向量：对接码 "41 42" + 收到 0x41 0x42 → 命中。
        var button = Btn("Hex 按钮", "41 42", PairingCodeTypes.Hex);
        var payload = new byte[] { 0x41, 0x42 };          // 文本恰好是 "AB"

        var match = PairingMatchService.Match(Encoding.UTF8.GetString(payload), payload, new[] { button }, Empty());

        match.Kind.Should().Be(PairingMatchKind.Button);
        match.MatchedCodeType.Should().Be(PairingCodeTypes.Hex);
    }

    [Fact]
    public void Hex_pairing_code_ignores_whitespace_and_case()
    {
        var button = Btn("Hex", " aa  BB\tcc ", PairingCodeTypes.Hex);
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };

        PairingMatchService.Match(string.Empty, payload, new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.Button, "空白与大小写都不影响 Hex 比较");
    }

    [Fact]
    public void Hex_pairing_code_does_not_strip_colon_or_dash_separators()
    {
        // ⚠️ 旧版既有行为：NormalizeHex **只去空白**，不像 TryParseMac 那样兼容 `:` / `-`。
        //    所以 Hex 对接码写成 "AA-BB-CC"（MAC 习惯写法）永远匹配不上。
        //    这一点与 WoL 的 MAC 解析规则不同，容易被误以为"应该能通"，故显式锁定。
        var button = Btn("Hex 带横杠", "AA-BB-CC-DD-EE-FF", PairingCodeTypes.Hex);
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };

        PairingMatchService.Match(string.Empty, payload, new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.None, "分隔符不会被剥离");

        DataEncoder.NormalizeHex("aa-bb-cc-dd-ee-ff").Should().Be("AA-BB-CC-DD-EE-FF");
        DataEncoder.TryParseMac("AA-BB-CC-DD-EE-FF").Should().NotBeNull("但 WoL 的 MAC 解析是兼容横杠的 —— 两处规则不同");
    }

    [Fact]
    public void Hex_pairing_code_does_not_match_different_bytes()
    {
        var button = Btn("Hex", "4142", PairingCodeTypes.Hex);

        PairingMatchService.Match("AC", new byte[] { 0x41, 0x43 }, new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.None);
    }

    [Fact]
    public void Hex_pairing_code_with_0x_prefix_does_not_match()
    {
        // ⚠️ 旧版既有行为（不是本次引入的缺陷）：NormalizeHex 只去空白 + 转大写，
        //    **不剥离 "0x"** —— 因此用户把 Hex 对接码写成 "0x4142" 时永远匹配不上
        //    （发送侧的 ParseHex 才会剥离 0x，两处规则不同）。此处锁定该行为。
        var button = Btn("Hex 带前缀", "0x4142", PairingCodeTypes.Hex);

        PairingMatchService.Match("AB", new byte[] { 0x41, 0x42 }, new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.None, "对接码不剥离 0x 前缀，与旧版 NormalizeHex 一致");

        DataEncoder.NormalizeHex("0x4142").Should().Be("0X4142");
    }

    [Fact]
    public void Hex_pairing_code_uses_raw_bytes_so_invalid_utf8_still_matches()
    {
        // 原始字节含非法 UTF-8 序列时，文本解码是有损的 —— 这正是必须用 Payload 比较的原因。
        var button = Btn("Hex", "FFFE", PairingCodeTypes.Hex);
        var payload = new byte[] { 0xFF, 0xFE };

        PairingMatchService.Match(Encoding.UTF8.GetString(payload), payload, new[] { button }, Empty()).Kind
            .Should().Be(PairingMatchKind.Button, "文本已损坏，但按原始字节比较仍然命中");
    }

    [Fact]
    public void Chinese_utf8_pairing_code_is_matched()
    {
        // §10.1 的正面用例：UTF-8 中文对接码在"UTF-8 解码 + UTF-8 比较"下必须命中。
        // （M1 用 ASCII 解码，中文会变 "??"，此例必然失败 —— 见 PairingListenerServiceTests 的回归。）
        var button = Btn("中文码", "开门");
        var payload = Encoding.UTF8.GetBytes("开门");

        var match = PairingMatchService.Match(Encoding.UTF8.GetString(payload), payload, new[] { button }, Empty());

        match.Kind.Should().Be(PairingMatchKind.Button);
        match.MatchedCode.Should().Be("开门");
    }

    [Fact]
    public void Gbk_bytes_do_not_match_a_utf8_pairing_code()
    {
        // 反面用例（诚实项）：设备若按 GBK 发中文，收包侧按 UTF-8 解码必然对不上 ——
        // 旧版同样如此。要求设备与对接码统一用 UTF-8 才能匹配。
        var button = Btn("中文码", "开门");
        var gbkBytes = DataEncoder.Encode(DataTypes.Ansi, "开门");

        PairingMatchService.IsPairingCodeMatch("开门", PairingCodeTypes.Utf8, Encoding.UTF8.GetString(gbkBytes), gbkBytes)
            .Should().BeFalse("GBK 字节按 UTF-8 解码是乱码，非 Hex 对接码无法命中");
    }

    // ═══════════════════════════════════════════════════════════
    // 旧版既有行为（quirk）：文本为空时动作级不匹配
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Empty_text_skips_action_level_matching_but_not_button_level()
    {
        // 旧版 TryMatchAction 开头即 `if (string.IsNullOrEmpty(code)) return false;`。
        // 后果：Hex 对接码 "00" 收到单字节 0x00 时，文本被 Trim 成空串 ——
        //   · 按钮级 / 子按钮级：能命中
        //   · 动作级：命中不了
        // 这是**既有行为**，这里显式锁定，避免后续被"顺手修好"而偏离参照实现。
        var payload = new byte[] { 0x00 };

        var buttonByButton = Btn("按钮级", "00", PairingCodeTypes.Hex);
        PairingMatchService.Match(string.Empty, payload, new[] { buttonByButton }, Empty()).Kind
            .Should().Be(PairingMatchKind.Button, "按钮级没有空文本判定");

        var buttonByAction = Btn("动作级", "", actions: Act("带码动作", "00", PairingCodeTypes.Hex));
        PairingMatchService.Match(string.Empty, payload, new[] { buttonByAction }, Empty()).Kind
            .Should().Be(PairingMatchKind.None, "动作级要求文本非空（旧版 TryMatchAction 的空值早退）");
    }

    // ═══════════════════════════════════════════════════════════
    // 边界
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Empty_catalog_never_matches()
    {
        PairingMatchService.Match("ANY", Bytes("ANY"), Array.Empty<ButtonConfig>(), Empty()).Kind
            .Should().Be(PairingMatchKind.None);
    }

    [Fact]
    public void Null_arguments_are_tolerated()
    {
        var act = () => PairingMatchService.Match(null, null, null, null);

        act.Should().NotThrow();
        act().Kind.Should().Be(PairingMatchKind.None);
    }

    [Fact]
    public void Null_entries_inside_lists_are_skipped()
    {
        // 反序列化畸形配置（[null]）不应让匹配崩掉。
        var button = Btn("正常", "OK");

#pragma warning disable CS8620 // 有意构造含 null 的列表
        var buttons = new List<ButtonConfig> { null!, button };
        var sets = new List<SubButtonSet> { null! };
#pragma warning restore CS8620

        PairingMatchService.Match("OK", Bytes("OK"), buttons, sets).Kind.Should().Be(PairingMatchKind.Button);
    }

    [Fact]
    public void SubButton_in_the_second_set_is_found()
    {
        var first = new SubButtonSet { Label = "集一", SubButtons = { Sub("子一", "A") } };
        var second = new SubButtonSet { Label = "集二", TargetIp = "10.2.2.2", TargetPort = 8002, SubButtons = { Sub("子二", "B") } };

        var match = PairingMatchService.Match("B", Bytes("B"), Array.Empty<ButtonConfig>(), new[] { first, second });

        match.OwnerLabel.Should().Be("子二");
        match.OverrideIp.Should().Be("10.2.2.2");
        match.OverridePort.Should().Be(8002);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static List<SubButtonSet> Empty() => new();

    private static ButtonAction[] Acts(params ButtonAction[] actions) => actions;

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static ButtonConfig Btn(
        string label,
        string pairingCode,
        string pairingCodeType = PairingCodeTypes.String,
        params ButtonAction[] actions)
        => new()
        {
            Label = label,
            PairingCode = pairingCode,
            PairingCodeType = pairingCodeType,
            Actions = actions.ToList(),
        };

    private static SubButton Sub(
        string label,
        string pairingCode,
        string pairingCodeType = PairingCodeTypes.Utf8,
        params ButtonAction[] actions)
        => new()
        {
            Label = label,
            PairingCode = pairingCode,
            PairingCodeType = pairingCodeType,
            Actions = actions.ToList(),
        };

    private static ButtonAction Act(
        string name,
        string pairingCode = "",
        string pairingCodeType = PairingCodeTypes.String)
        => new()
        {
            Name = name,
            Protocol = TransportProtocols.Udp,
            TargetIp = "127.0.0.1",
            TargetPort = 15000,
            DataType = DataTypes.Utf8,
            DataContent = name,
            PairingCode = pairingCode,
            PairingCodeType = pairingCodeType,
        };
}
