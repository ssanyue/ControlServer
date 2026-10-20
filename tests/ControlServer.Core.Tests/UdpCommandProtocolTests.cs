using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// 客户机 UDP 指令通道的<b>线路协议</b>（M4.5；2026-10-13 改为纯明文 + 来源 IP 黑名单）。
///
/// <para>
/// 这条通道是<b>本项目里唯一一个客户机主动开着的入站端口</b>，而它现在是<b>明文</b>的：
/// 谁能把数据报送进那个端口，谁就能关机。这是已批准的取舍，所以这里的断言分两半 ——
/// <b>"该认的一定要认全"</b>与<b>"不该认的一个都不能放过"</b>。
/// </para>
///
/// <para>
/// 改版前这里是 HMAC 验签（<c>CS1|指令|时间戳|nonce|签名</c>），代价是
/// "用任意工具发一个 <c>DOWN</c>"永远不生效 —— 现场表现为"指令没反应"，
/// 而根因（段数不对）在发送端完全看不见。这套签名机制、时间窗、
/// nonce 防重放与通道密钥上报<b>已整体删除</b>；下面的用例同时承担"别把它们加回来"的守卫职责。
/// </para>
/// </summary>
public sealed class UdpCommandProtocolTests
{
    // ═══════════════════════════════════════════════════════════
    // ① 六个明文短码
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("DOWN", AgentCommandNames.Power, AgentPowerActions.Shutdown)]
    [InlineData("RESTART", AgentCommandNames.Power, AgentPowerActions.Restart)]
    [InlineData("+", AgentCommandNames.Volume, AgentVolumeActions.Up)]
    [InlineData("-", AgentCommandNames.Volume, AgentVolumeActions.Down)]
    [InlineData("0", AgentCommandNames.Volume, AgentVolumeActions.Mute)]
    [InlineData("1", AgentCommandNames.Volume, AgentVolumeActions.Unmute)]
    public void The_six_plain_tokens_map_to_the_expected_commands(string token, string name, string argument)
    {
        UdpCommandProtocol.TryResolveCommand(token, out var actualName, out var actualArgument)
            .Should().BeTrue();

        actualName.Should().Be(name);
        actualArgument.Should().Be(argument);
    }

    /// <summary>
    /// <c>REBOOT</c> 是<b>历史别名</b>：改版只把 <c>RESTART</c> 立为正式写法，
    /// 并没有把 <c>REBOOT</c> 作废 —— 作废它只会让现场已有的发送脚本"突然失灵"。
    /// </summary>
    [Fact]
    public void The_legacy_REBOOT_alias_still_resolves_to_a_restart()
    {
        UdpCommandProtocol.TryResolveCommand("REBOOT", out var name, out var argument)
            .Should().BeTrue();

        name.Should().Be(AgentCommandNames.Power);
        argument.Should().Be(AgentPowerActions.Restart);
    }

    /// <summary>现场手打 <c>down</c> 是常态，大小写不该有影响。</summary>
    [Theory]
    [InlineData("down")]
    [InlineData("Down")]
    [InlineData("dOwN")]
    public void Tokens_are_case_insensitive(string token)
    {
        UdpCommandProtocol.TryResolveCommand(token, out _, out var argument).Should().BeTrue();
        argument.Should().Be(AgentPowerActions.Shutdown);
    }

    /// <summary>本项目的 <c>name:argument</c> 写法（中心网页与既有配置文件用它）。</summary>
    [Theory]
    [InlineData("power:shutdown", AgentCommandNames.Power, AgentPowerActions.Shutdown)]
    [InlineData("volume:up", AgentCommandNames.Volume, AgentVolumeActions.Up)]
    [InlineData("VOLUME:Unmute", AgentCommandNames.Volume, AgentVolumeActions.Unmute)]
    public void The_project_style_tokens_are_accepted(string token, string name, string argument)
    {
        UdpCommandProtocol.TryResolveCommand(token, out var actualName, out var actualArgument)
            .Should().BeTrue();

        actualName.Should().Be(name);
        actualArgument.Should().Be(argument);
    }

    // ═══════════════════════════════════════════════════════════
    // ② 反向映射：规范指令 → 线路 token（2026-10-15）
    //
    // 🔥 它是"离线也要把指令发出去"那条路的地基：中心侧的关机 / 重启按钮原先只走
    //    SignalR（要求长连接），现在长连接不在时要改走 UDP，而 UDP 线路上跑的是 token。
    //    这里钉死的是**往返一致**：TryFormat 写出去的，TryResolveCommand 必须认得。
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(AgentCommandNames.Power, AgentPowerActions.Shutdown, "DOWN")]
    [InlineData(AgentCommandNames.Power, AgentPowerActions.Restart, "RESTART")]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Up, "+")]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Down, "-")]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Mute, "0")]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Unmute, "1")]
    public void Formatting_a_command_gives_the_expected_token(string name, string argument, string expected)
    {
        UdpCommandProtocol.TryFormat(name, argument, out var token).Should().BeTrue();

        token.Should().Be(expected);
    }

    /// <summary>
    /// 🔥 往返一致 —— 这条比上面那组逐项对照更重要：两张表各写一份时，
    /// "改了一处"不会有任何编译错误，而症状是"中心说已发出、机器上什么都没发生"。
    /// </summary>
    [Theory]
    [InlineData(AgentCommandNames.Power, AgentPowerActions.Shutdown)]
    [InlineData(AgentCommandNames.Power, AgentPowerActions.Restart)]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Up)]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Down)]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Mute)]
    [InlineData(AgentCommandNames.Volume, AgentVolumeActions.Unmute)]
    public void Every_formattable_command_round_trips_back(string name, string argument)
    {
        UdpCommandProtocol.TryFormat(name, argument, out var token).Should().BeTrue();

        UdpCommandProtocol.TryResolveCommand(token, out var backName, out var backArgument)
            .Should().BeTrue($"「{token}」是这里自己写出去的，客户机那边必须认得");

        backName.Should().Be(name);
        backArgument.Should().Be(argument);
    }

    /// <summary>
    /// 不在 UDP 协议支持之列的（例如「重启 Agent 服务」）必须返回 false ——
    /// 它是 <c>/reboot</c> 那种"只能经长连接下发"的指令，直接回一句"现在发不出去"
    /// 比编一个客户机不认的 token 发出去强得多。
    /// </summary>
    [Theory]
    [InlineData(AgentCommandNames.Restart, null)]               // 重启 **Agent 服务**（不是整机）
    [InlineData(AgentCommandNames.RefreshConfig, null)]
    [InlineData(AgentCommandNames.Power, null)]                 // 缺参数
    [InlineData(AgentCommandNames.Power, "halt")]               // 参数不合法
    [InlineData(AgentCommandNames.Volume, "loud")]
    [InlineData("something-new", "x")]
    [InlineData(null, "shutdown")]
    [InlineData("", "shutdown")]
    public void A_command_outside_the_udp_vocabulary_cannot_be_formatted(string? name, string? argument)
    {
        UdpCommandProtocol.TryFormat(name, argument, out var token).Should().BeFalse();

        token.Should().BeEmpty("失败时不能给出半个 token 让调用方误以为可以发");
    }

    // ═══════════════════════════════════════════════════════════
    // ③ 不该认的
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("HELLO")]
    [InlineData("SHUTDOWN")]
    [InlineData("POWEROFF")]
    [InlineData("++")]
    [InlineData("2")]
    public void Unknown_tokens_are_rejected(string token)
    {
        UdpCommandProtocol.TryResolveCommand(token, out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// 🔥 参数也要在合法集合内。只校验"命名空间对不对"会让 <c>power:xxx</c> 被放行到执行侧才被拒 ——
    /// 表现为"页面说发出去了、机器上什么都没发生"，那是本题里最难查的一种形态。
    /// </summary>
    [Theory]
    [InlineData("power:xxx")]
    [InlineData("volume:loud")]
    [InlineData("volume:")]
    [InlineData("power:")]
    [InlineData(":shutdown")]
    [InlineData("other:shutdown")]
    public void Tokens_with_a_bad_namespace_or_argument_are_rejected(string token)
    {
        UdpCommandProtocol.TryResolveCommand(token, out _, out _).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // ④ TryParse：报文层的容错与上限
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void A_plain_datagram_parses_and_reports_the_original_token()
    {
        UdpCommandProtocol.TryParse("DOWN", out var command, out var error).Should().BeTrue();

        error.Should().BeNull();
        command.Should().NotBeNull();
        command!.Name.Should().Be(AgentCommandNames.Power);
        command.Argument.Should().Be(AgentPowerActions.Shutdown);
        command.Token.Should().Be("DOWN", "日志要用它回显「到底收到了什么」");
    }

    /// <summary>
    /// 🔥 现场常用 <c>echo DOWN | nc -u -w1 主机 8090</c> —— 末尾会带一个换行。
    /// 因为一个换行就把指令丢掉，是最没道理的一种"无效果"，所以这里必须放行。
    /// </summary>
    [Theory]
    [InlineData("DOWN\n")]
    [InlineData("DOWN\r\n")]
    [InlineData("  DOWN \t")]
    [InlineData("\uFEFFDOWN")]
    public void Trailing_whitespace_and_a_BOM_are_tolerated(string payload)
    {
        UdpCommandProtocol.TryParse(payload, out var command, out _).Should().BeTrue();

        command!.Name.Should().Be(AgentCommandNames.Power);
        command.Token.Should().Be("DOWN", "回显的应该是归一化后的 token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Blank_payloads_are_rejected_with_a_readable_reason(string payload)
    {
        UdpCommandProtocol.TryParse(payload, out var command, out var error).Should().BeFalse();

        command.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unknown_payload_is_rejected_and_the_reason_quotes_what_arrived()
    {
        UdpCommandProtocol.TryParse("HELLO", out var command, out var error).Should().BeFalse();

        command.Should().BeNull();
        error.Should().Contain("HELLO", "丢弃日志里要能看见到底收到了什么，否则没法排查谁在乱发");
    }

    /// <summary>
    /// 报文长度上限：UDP 数据报最大 64 KB，一个灌包的脚本能每秒送几万条几十 KB 的垃圾。
    /// 没有上限的话，"那条丢弃原因"会被一坨乱码塞满。
    /// </summary>
    [Fact]
    public void An_over_long_payload_is_rejected()
    {
        var payload = new string('A', UdpCommandProtocol.MaxTokenLength + 1);

        UdpCommandProtocol.TryParse(payload, out var command, out var error).Should().BeFalse();

        command.Should().BeNull();
        error.Should().Contain("过长");
    }

    [Fact]
    public void A_payload_exactly_at_the_limit_is_still_evaluated()
    {
        // 刚好到上限：不是"过长"，而是"认不出的指令" —— 两者是不同的失败原因，不能混。
        var payload = new string('A', UdpCommandProtocol.MaxTokenLength);

        UdpCommandProtocol.TryParse(payload, out _, out var error).Should().BeFalse();

        error.Should().Contain("不认识");
    }

    // ═══════════════════════════════════════════════════════════
    // ⑤ 来源 IP 黑名单
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void An_empty_blacklist_blocks_nobody()
    {
        UdpCommandProtocol.IsSourceBlocked(Array.Empty<string>(), "192.168.1.5").Should().BeFalse();
        UdpCommandProtocol.IsSourceBlocked(null, "192.168.1.5").Should().BeFalse(
            "「没配」与「配了空」必须同义 —— 默认就是「任意来源皆可」");
    }

    [Fact]
    public void A_listed_source_is_blocked()
    {
        UdpCommandProtocol.IsSourceBlocked(new[] { "192.168.1.5" }, "192.168.1.5").Should().BeTrue();
    }

    [Fact]
    public void An_unlisted_source_passes_through()
    {
        UdpCommandProtocol.IsSourceBlocked(new[] { "192.168.1.5" }, "192.168.1.6").Should().BeFalse();
    }

    /// <summary>
    /// ⚠️ 双栈 socket 上收到 IPv4 报文时，<c>RemoteEndPoint</c> 常常是
    /// <c>::ffff:192.168.1.5</c> 这个形态。不归一化就会"明明拉黑了却拦不住"。
    /// </summary>
    [Fact]
    public void An_IPv4_mapped_IPv6_source_matches_its_IPv4_entry()
    {
        UdpCommandProtocol.IsSourceBlocked(new[] { "192.168.1.5" }, "::ffff:192.168.1.5")
            .Should().BeTrue();

        UdpCommandProtocol.IsSourceBlocked(new[] { "::ffff:192.168.1.5" }, "192.168.1.5")
            .Should().BeTrue();
    }

    [Fact]
    public void Surrounding_whitespace_in_entries_does_not_defeat_the_match()
    {
        UdpCommandProtocol.IsSourceBlocked(new[] { "  192.168.1.5 " }, "192.168.1.5").Should().BeTrue();
    }

    /// <summary>
    /// 🔥 <b>源地址解析失败 ⇒ 拦下（fail-closed）</b>。
    /// 这条路本身就是"我们无法证明它不在黑名单里"；解析不出来的地址意味着连判据都没有，
    /// 此时放行等于把黑名单变成装饰。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    public void An_unparseable_source_is_blocked_when_a_blacklist_exists(string? remoteIp)
    {
        UdpCommandProtocol.IsSourceBlocked(new[] { "192.168.1.5" }, remoteIp).Should().BeTrue();
    }

    /// <summary>但没有黑名单时，源地址解析不出来也不该被拦（否则等于凭空拒收）。</summary>
    [Fact]
    public void An_unparseable_source_is_not_blocked_when_there_is_no_blacklist()
    {
        UdpCommandProtocol.IsSourceBlocked(Array.Empty<string>(), "not-an-ip").Should().BeFalse();
    }

    /// <summary>黑名单里的坏条目不该让别的条目失效（校验层会拦它们，但这里要能扛住）。</summary>
    [Fact]
    public void A_malformed_entry_does_not_break_the_others()
    {
        UdpCommandProtocol.IsSourceBlocked(new[] { "不是IP", "192.168.1.5" }, "192.168.1.5")
            .Should().BeTrue();
    }
}
