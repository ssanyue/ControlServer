using ControlServer.Contracts;
using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// M4.1 的**纯逻辑**回归：在线状态判定、密钥生成、发现协议。
/// <para>
/// 这三块的共同点是"算错了也不会有异常"—— 页面上只表现为"某台机器偶尔变灰"、
/// "客户端广播了但服务端没反应"。靠肉眼几乎无法定位，所以必须用单测钉住。
/// </para>
/// </summary>
public sealed class AgentProtocolAndPresenceTests
{
    // ═══════════════════════════════════════════════════════════
    // 在线状态判定
    // ═══════════════════════════════════════════════════════════

    /// <summary>生产默认阈值（见 <c>AgentOptions</c>）：心跳 10s / 不稳定 30s / 离线 75s。</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Offline = TimeSpan.FromSeconds(75);

    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Revoked_beats_every_time_based_state()
    {
        // 刚从吊销那一刻算起只有 1 秒 —— 时间判据会说"在线"，但吊销必须压过它。
        AgentPresence.Classify(Now.AddSeconds(-1), Now, Stale, Offline, isBlocked: true)
            .Should().Be(AgentStates.Blocked);
    }

    [Fact]
    public void Missing_last_seen_means_never_connected_not_offline()
    {
        // 这两种状态导出的下一步动作完全不同：前者查"客户端装起来了吗"，
        // 后者查"机器/网络怎么了"。合并成一个会让现场排查从一开始就走错方向。
        AgentPresence.Classify(null, Now, Stale, Offline, isBlocked: false)
            .Should().Be(AgentStates.Never);
    }

    [Theory]
    [InlineData(0, AgentStates.Online)]
    [InlineData(10, AgentStates.Online)]
    [InlineData(30, AgentStates.Online)]        // 边界：恰好等于 staleAfter 仍算在线
    [InlineData(31, AgentStates.Unstable)]
    [InlineData(75, AgentStates.Unstable)]      // 边界：恰好等于 offlineAfter 仍算不稳定
    [InlineData(76, AgentStates.Offline)]
    [InlineData(600, AgentStates.Offline)]
    public void State_follows_the_heartbeat_age_thresholds(int ageSeconds, string expected)
    {
        var lastSeen = Now.AddSeconds(-ageSeconds);

        AgentPresence.Classify(lastSeen, Now, Stale, Offline, isBlocked: false)
            .Should().Be(expected);
    }

    [Fact]
    public void A_client_clock_running_ahead_must_not_be_reported_offline()
    {
        // 客户机时钟快于服务端时 age 为负。若不加这道保护，一台时钟不准的机器
        // 会永远显示成"离线"，而它其实每次心跳都准时到达。
        AgentPresence.Classify(Now.AddMinutes(5), Now, Stale, Offline, isBlocked: false)
            .Should().Be(AgentStates.Online);
    }

    /// <summary>
    /// 🔥 <b>2026-10-15 修的那个 BUG</b>：<b>已知长连接已断时，"在线"这个结论必须失效</b>。
    ///
    /// <para>
    /// 症状（现场原话）："客户端明明是绿的，一点关机却说客户端离线、无法下发指令"。
    /// </para>
    /// <para>
    /// 成因：客户机断开的那一刻，中心会把"最后心跳"<b>钉成断开时刻</b>
    /// （<c>AgentHostRegistry.MarkDisconnected</c>，为了让离线计时有个确定起点），
    /// 于是 age≈0 ⇒ 只看时间的旧判定说"在线"；而同一时刻下发用的闸门
    /// （<c>ConnectionIdOf</c>）已经变成 null ⇒ 点下去必然 409。
    /// 客户机重连退避 1→2→4→…→60 秒，所以这个自相矛盾的窗口能长达几十秒。
    /// </para>
    /// <para>
    /// 判定：把"连接在不在"这条<b>中心唯一确定知道的实时事实</b>喂进来 ——
    /// 断开的机器最多只能显示"不稳定"，不可能显示"在线"。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, AgentStates.Unstable)]       // 断开即被钉成"刚刚" —— 旧判定在这里会说 Online
    [InlineData(1, AgentStates.Unstable)]
    [InlineData(30, AgentStates.Unstable)]      // 边界：恰好等于 staleAfter，仍只能是"不稳定"
    [InlineData(75, AgentStates.Unstable)]      // 边界：恰好等于 offlineAfter
    [InlineData(76, AgentStates.Offline)]
    [InlineData(3600, AgentStates.Offline)]
    public void A_known_dead_long_connection_can_never_be_online(int ageSeconds, string expected)
    {
        AgentPresence
            .Classify(Now.AddSeconds(-ageSeconds), Now, Stale, Offline, isBlocked: false, longConnection: false)
            .Should().Be(expected);
    }

    /// <summary>
    /// 连接已断 + 客户机时钟快于服务端（age 为负）同样只能是"不稳定"：
    /// 负 age 那道保护是给"时钟不准"用的，不该顺手把"连接已经断了"这条事实也绕过去。
    /// </summary>
    [Fact]
    public void A_dead_connection_is_not_rescued_by_a_clock_running_ahead()
    {
        AgentPresence
            .Classify(Now.AddMinutes(5), Now, Stale, Offline, isBlocked: false, longConnection: false)
            .Should().Be(AgentStates.Unstable);
    }

    /// <summary>
    /// ⚠️ 反向的一条：<b>"连接还在"不额外放宽时间判定</b>。
    /// <para>
    /// 一条僵尸连接（socket 还挂着、心跳已经停了）恰恰是最需要报警的故障 ——
    /// 只看连接就判"在线"会把这种故障藏在绿点后面，那是同一个坑的另一半。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, AgentStates.Online)]
    [InlineData(31, AgentStates.Unstable)]
    [InlineData(76, AgentStates.Offline)]
    public void A_live_connection_does_not_relax_the_heartbeat_thresholds(int ageSeconds, string expected)
    {
        AgentPresence
            .Classify(Now.AddSeconds(-ageSeconds), Now, Stale, Offline, isBlocked: false, longConnection: true)
            .Should().Be(expected);
    }

    /// <summary>
    /// <c>null</c> = "不知道"（中心刚起来、这台机器在本次运行期里没有任何记录）⇒
    /// 沿用只按时间的旧行为，绝不能当成"确定它没连着"。
    /// <para>
    /// ⚠️ 这两种情况在代码里只差一个字符（<c>live?.ConnectionId is not null</c> 会把"不知道"
    /// 静默地算成 false），而后果是"中心一重启，所有机器集体变黄"。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, AgentStates.Online)]
    [InlineData(31, AgentStates.Unstable)]
    [InlineData(76, AgentStates.Offline)]
    public void An_unknown_connection_state_falls_back_to_the_time_based_judgement(int ageSeconds, string expected)
    {
        AgentPresence
            .Classify(Now.AddSeconds(-ageSeconds), Now, Stale, Offline, isBlocked: false, longConnection: null)
            .Should().Be(expected);
    }

    /// <summary>拉黑优先级压过一切：连接断没断都不影响它显示"已拉黑"。</summary>
    [Fact]
    public void Blocked_beats_a_dead_connection_too()
    {
        AgentPresence
            .Classify(Now.AddSeconds(-1), Now, Stale, Offline, isBlocked: true, longConnection: false)
            .Should().Be(AgentStates.Blocked);
    }

    /// <summary>从未连接过（没有心跳）时，"连接断了"这条事实不该把它从"从未连接"改成"离线"。</summary>
    [Fact]
    public void Never_connected_still_wins_over_a_dead_connection()
    {
        AgentPresence
            .Classify(null, Now, Stale, Offline, isBlocked: false, longConnection: false)
            .Should().Be(AgentStates.Never);
    }

    /// <summary>
    /// 把"关机 ≤ 90 秒变离线"这条验收钉在**具体数值**上。
    /// <para>
    /// 它同时解释了 <c>OfflineAfterSeconds</c> 为什么是 75 而不是"5 分钟"：
    /// 心跳 10s + 离线阈值 75s = 85s &lt; 90s，留了 5 秒余量给调度抖动。
    /// 谁把 75 调大，这条用例会立刻变红并说明原因。
    /// </para>
    /// </summary>
    [Fact]
    public void The_default_thresholds_satisfy_the_ninety_second_offline_acceptance()
    {
        const int heartbeatSeconds = 10;
        var offlineAfterSeconds = (int)Offline.TotalSeconds;

        (heartbeatSeconds + offlineAfterSeconds).Should().BeLessThanOrEqualTo(
            90,
            "验收要求「客户端进程被杀后 90 秒内页面显示离线」");

        // 最坏情况：最后一跳刚好在 T 时刻，进程在 T+9.9s 被杀。
        // 此时从"断开那一刻"起算（MarkDisconnected 会把 LastSeenAt 钉成断开时刻），
        // 所以实际耗时就是 offlineAfter 本身 —— 上面那条求和是更保守的界。
        AgentPresence.Classify(Now.AddSeconds(-offlineAfterSeconds - 1), Now, Stale, Offline, isBlocked: false)
            .Should().Be(AgentStates.Offline);
    }

    [Theory]
    [InlineData(AgentStates.Online, true)]
    [InlineData(AgentStates.Unstable, true)]
    [InlineData(AgentStates.Offline, false)]
    [InlineData(AgentStates.Never, false)]
    [InlineData(AgentStates.Blocked, false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsActive_only_covers_online_and_unstable(string? state, bool expected)
    {
        AgentPresence.IsActive(state).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "从未连接")]
    [InlineData(-30, "刚刚")]          // 时钟偏差：负数不能显示成"-30 秒前"
    [InlineData(59, "59 秒前")]
    [InlineData(60, "1 分钟前")]
    [InlineData(3599, "59 分钟前")]
    [InlineData(3600, "1 小时前")]
    [InlineData(86399, "23 小时前")]
    [InlineData(86400, "1 天前")]
    public void DescribeAge_reads_as_human_language(int? ageSeconds, string expected)
    {
        // ageSeconds 是"距今多少秒"，所以 lastSeen 要往**过去**推；
        // 传入负数（如 -30）表示客户机时钟快了 30 秒。
        var lastSeen = ageSeconds is null ? (DateTimeOffset?)null : Now.AddSeconds(-ageSeconds.Value);

        AgentPresence.DescribeAge(lastSeen, Now).Should().Be(expected);
    }

    [Fact]
    public void Every_known_state_has_a_chinese_display_name()
    {
        var states = new[]
        {
            AgentStates.Online,
            AgentStates.Unstable,
            AgentStates.Offline,
            AgentStates.Never,
            AgentStates.Blocked,
        };

        // ⚠️ Equal 带 params 重载，必须显式给数组（否则第二个参数会被当成期望的第二个元素）。
        states.Select(AgentStates.DisplayName).Should().Equal(
            new[] { "在线", "不稳定", "离线", "等待连接", "已拉黑" },
            "状态的中文名由服务端下发，前端不维护对照表");

        // 未知状态原样返回，不抛异常 —— 将来服务端加了新状态，老前端顶多显示英文标识，
        // 不该整页白屏。
        AgentStates.DisplayName("something-new").Should().Be("something-new");
    }

    // ═══════════════════════════════════════════════════════════
    // 密钥
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void NewSecret_is_url_safe_and_unique()
    {
        var secrets = Enumerable.Range(0, 200).Select(_ => AgentKeys.NewSecret()).ToArray();

        secrets.Should().OnlyHaveUniqueItems("32 字节随机数碰不出重复");

        foreach (var secret in secrets)
        {
            // 这些是 Base64Url 的立身之本：直接进 URL 查询串 / 命令行而无需转义。
            secret.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
            secret.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        }

        // 32 字节 → 43 个 Base64 字符（去掉补位的 '='）。
        secrets[0].Length.Should().Be(43);
    }

    [Fact]
    public void Hash_is_lowercase_sha256_hex_and_deterministic()
    {
        var secret = AgentKeys.NewSecret();
        var hash = AgentKeys.Hash(secret);

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$", "库里按它做唯一索引，大小写必须稳定");
        AgentKeys.Hash(secret).Should().Be(hash, "同一输入必须得到同一哈希（认证时靠它反查）");
        AgentKeys.Hash(secret + "x").Should().NotBe(hash);
    }

    // ═══════════════════════════════════════════════════════════
    // 发现协议
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Discover_message_round_trips()
    {
        var text = AgentDiscoveryProtocol.BuildDiscover("MACHINE-ABC123", "0.1.0");

        text.Should().StartWith(AgentProtocol.DiscoverKeyword);

        AgentDiscoveryProtocol.TryParseDiscover(text, out var message, out var error)
            .Should().BeTrue(error);

        message.MachineKey.Should().Be("MACHINE-ABC123");
        message.AgentVersion.Should().Be("0.1.0");
    }

    [Fact]
    public void Here_message_round_trips_with_seven_fields()
    {
        // 字段数写死在 7：客户端靠固定下标取值，多一个少一个都会静默取错。
        var text = AgentDiscoveryProtocol.BuildHere(new AgentHereMessage(
            AgentProtocol.Product, "0.1.0", 8080, 9999, 9998, "server-1"));

        AgentDiscoveryProtocol.TryParseHere(text, out var message, out var error)
            .Should().BeTrue(error);

        message.Product.Should().Be(AgentProtocol.Product);
        message.Version.Should().Be("0.1.0");
        message.HttpPort.Should().Be(8080);
        message.PairingPort.Should().Be(9999);
        message.DiscoveryPort.Should().Be(9998);
        message.ServerId.Should().Be("server-1");
    }

    [Fact]
    public void Here_reply_carries_no_credentials()
    {
        // 应答是整个广播域都看得到的。它只能含定位信息 ——
        // 谁往里加令牌，谁就把批量装机的凭据公开了。
        var text = AgentDiscoveryProtocol.BuildHere(new AgentHereMessage(
            AgentProtocol.Product, "0.1.0", 8080, 9999, 9998, "server-1"));

        text.Should().NotContainEquivalentOf("token");
        text.Should().NotContainEquivalentOf("key");
        text.Should().NotContainEquivalentOf("secret");
        text.Should().NotContainEquivalentOf("password");
    }

    [Theory]
    [InlineData("")]                                              // 空报文
    [InlineData("   ")]                                           // 全空格
    [InlineData("CS-DISCOVER")]                                   // 字段数不足
    [InlineData("CS-DISCOVER machine")]                           // 少一个字段
    [InlineData("CS-DISCOVER machine 0.1.0 extra")]               // 多一个字段
    [InlineData("CS-HELLO machine 0.1.0")]                        // 关键字不对（大小写不敏感但仍要不匹配）
    [InlineData("CS-DISCOVER 0.1.0")]                             // 缺机器标识
    public void Discover_parsing_rejects_malformed_datagrams_without_throwing(string text)
    {
        // 宽容但不猜：解析失败一律 false + 可读原因，绝不抛异常
        //（它跑在 UDP 收包循环里，抛出去会打断整个监听）。
        var ok = AgentDiscoveryProtocol.TryParseDiscover(text, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace("失败必须给出可读原因，否则现场无从下手");
    }

    [Fact]
    public void Discover_parsing_tolerates_trailing_line_breaks()
    {
        // UDP 报文尾部带 \r\n 是常见误操作（从文件 / echo 里发出来的），
        // 不该因此整包作废。
        AgentDiscoveryProtocol.TryParseDiscover("CS-DISCOVER m1 0.1.0\r\n", out var message, out var error)
            .Should().BeTrue(error);

        message.MachineKey.Should().Be("m1");
    }

    [Fact]
    public void Discover_parsing_rejects_oversized_datagrams()
    {
        // 广播端口对整个局域网开放，任何主机都能往这里灌数据。
        // 限长是为了不被超大包拖住 —— 连解析都不做。
        var huge = "CS-DISCOVER " + new string('x', AgentProtocol.MaxDatagramBytes) + " 0.1.0";

        AgentDiscoveryProtocol.TryParseDiscover(huge, out _, out var error).Should().BeFalse();
        error.Should().Contain(AgentProtocol.MaxDatagramBytes.ToString());
    }

    [Fact]
    public void Fields_with_spaces_are_sanitized_so_the_field_count_stays_fixed()
    {
        // 空格是分隔符。机器名带空格（Windows 计算机名不会，但机器码派生实现可能变）
        // 会把一个字段切成两个，接收端字段数就对不上了 —— 必须在构造端就换成 '_'。
        var text = AgentDiscoveryProtocol.BuildDiscover("my machine", "0.1.0");

        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);

        AgentDiscoveryProtocol.TryParseDiscover(text, out var message, out _).Should().BeTrue();
        message.MachineKey.Should().Be("my_machine");
    }

    [Fact]
    public void Discovery_port_does_not_collide_with_the_pairing_port()
    {
        // 对码监听占用 9999。两者语义完全不同（一个收对接码、一个收发现广播），
        // 抢同一个端口会让其中一方静默失效。
        AgentProtocol.DefaultDiscoveryPort.Should().NotBe(9999);
        AgentProtocol.DefaultDiscoveryPort.Should().Be(9998);
    }
}
