using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Agent;
using ControlServer.Agent.Diagnostics;
using ControlServer.Agent.Tasks;
using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 客户机 <b>UDP 指令通道</b>的端到端测试（M4.5；2026-10-13 改为纯明文 + 黑名单）：
/// <b>真的开一个 UDP socket</b>，真的把数据报发过去，验证逐关行为。
///
/// <para>
/// 这是本项目里<b>唯一一个客户机主动开着的入站端口</b>。它现在是<b>明文</b>的 ——
/// 也就是说，"同网段任意一台机器发一个 <c>DOWN</c> 就能把这台设备关掉"是<b>已批准的取舍</b>，
/// 不是缺陷。因此这里的断言重点分两半：
/// </para>
/// <list type="bullet">
///   <item><b>该通的一定要通</b>：明文短码必须能被识别并执行，一个尾随换行都不能把指令吃掉
///         （这正是改版前"指令无效果"的根因）；</item>
///   <item><b>该拦的一定要拦住</b>：黑名单里的来源、认不出的字符串、超长报文，一律不执行。</item>
/// </list>
///
/// <para>
/// <b>绝不真的关机</b>：电源与音量都注入桩实现。
/// 桩不只是"避免副作用"，它同时也是断言的手段 ——
/// 记录到桩上的调用次数就是"这条报文到底被放行了没有"的唯一事实。
/// </para>
///
/// <para><b>三道关卡</b>（<c>UdpCommandListener</c> 类注释）：</para>
/// <list type="number">
///   <item>本机开关 —— <c>Agent:Tasks:Enabled=false</c> 时<b>根本不绑端口</b>；</item>
///   <item>来源 IP 黑名单；</item>
///   <item>报文解析（<c>UdpCommandProtocol.TryParse</c>）。</item>
/// </list>
///
/// <para>
/// 🔥 <b>用哪个端口</b>（2026-10-15）：生产那个"回落端口"是 <b>8090</b>，
/// 而开发机上随时可能装着一个<b>开机自启的真 Agent</b> 正占着它 ——
/// 用例若真去绑它，红绿就取决于"这台机器上装了什么"，而不是代码对不对。
/// 所以这组用例一律改用<b>当时空闲的端口</b>（见 <see cref="Harness"/>），
/// 而"生产那个回落端口确实是 8090"由一条<b>只读属性、不绑端口</b>的用例单独钉住。
/// </para>
/// </summary>
public sealed class UdpCommandListenerTests
{
    // ═══════════════════════════════════════════════════════════
    // 第 1 关：本机开关与配置
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <c>Enabled=false</c> 的正确语义是<b>"根本不绑端口"</b>，不是"绑定了但丢弃"。
    /// 后者在 <c>netstat</c> 里仍能看到一个开着的端口 —— 那等于"关了还在听"，
    /// 与安全审计时看到的完全不是一回事。
    /// </summary>
    [Fact]
    public async Task Tasks_disabled_never_binds_a_port_at_all()
    {
        await using var harness = new Harness(tasksEnabled: false);

        ApplyConnected(harness.Listener, Config(FreeUdpPort()));

        harness.Listener.IsRunning.Should().BeFalse();
        harness.Listener.Port.Should().BeNull();
        harness.SawLog("停用任务下发").Should().BeTrue("要留下一句可读的原因，否则现场只会看到'端口没开'");
    }

    /// <summary>
    /// 🔥 <b>"还没有配置" = 用默认值（启用 + 8090），不是"当作关闭"</b>（2026-10-14 改）。
    ///
    /// <para>
    /// 旧语义把 <c>null</c> 判成"中心下发的配置没有启用 UDP 指令通道"⇒ 压根不绑端口，
    /// 于是两类机器永远收不到指令，而现场看到的只是"指令没反应"：
    /// </para>
    /// <list type="bullet">
    ///   <item>装完还没拉到配置的那段时间（服务端不可达 / 首次注册中）；</item>
    ///   <item>中心侧<b>从未保存过</b>它的配置（<c>FindAsync</c> 返回 null）。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ 这里绑的是<b>回落端口</b>（生产＝<see cref="AgentConfigDefaults.DefaultUdpPort"/> 8090）。
    /// 用例<b>不</b>硬绑那个 8090，而是让宿主换一个空闲端口
    /// （<see cref="Harness"/> 的 <c>useProductionDefaultPort</c>）：否则这组用例的成败
    /// 会取决于"这台开发机上有没有装开机自启的真 Agent"——
    /// 那种红说的是机器上装了什么，不是代码对不对（2026-10-15 修）。
    /// <b>"生产那个回落端口到底是几"由下面 <c>The_default_port_is_the_one_the_field_scripts_aim_at</c> 单独钉住。</b>
    /// 失败时把 <c>StateText</c> 打进消息里，好一眼区分"语义错了"与"端口真被占了"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_missing_config_defaults_to_enabled_on_the_default_port()
    {
        await using var harness = new Harness();

        ApplyConnected(harness.Listener, null);

        harness.Listener.IsRunning.Should().BeTrue(
            $"还没有配置就该按默认值起（启用 + 回落端口 {harness.FallbackPort}）—— "
            + $"否则'装完还没拉到配置'这段时间是聋的。当前状态：{harness.Listener.StateText}");

        harness.Listener.Port.Should().Be(harness.FallbackPort);

        // 真的收得到（不是"绑上了但没人处理"）。
        await SendAsync(harness.FallbackPort, "RESTART");
        await WaitUntil(() => harness.Power.RestartCount == 1);

        harness.Power.RestartCount.Should().Be(1);
    }

    /// <summary>
    /// 🔥 <b>回落端口在生产里就是 8090</b> —— 现场那些发送脚本指着它
    /// （<c>echo DOWN | nc -u 主机 8090</c>），文档里也写着它。
    ///
    /// <para>
    /// 这条用例是上面那批"注入临时端口"用例的<b>另一半</b>：注入只改了"用例绑哪个端口"，
    /// 绝不能让"生产默认值是几"悄悄漂掉。链条两环都钉住：
    /// <b>① 常量是 8090</b>；<b>② 不注入时监听器用的就是那个常量</b>
    /// （也就是 <c>Program.cs</c> 的注册不传第 5 个参数 = 走生产默认值）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它<b>只读属性、从不 Apply</b> ⇒ <b>不会真去绑 8090</b>，
    /// 所以在本机装着一个占用 8090 的真 Agent 时它照样是绿的（这正是它与旧写法的区别）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_default_port_is_the_one_the_field_scripts_aim_at()
    {
        AgentConfigDefaults.DefaultUdpPort.Should().Be(8090, "现场发送脚本与文档里的 `nc -u 主机 8090` 指着这个数");

        await using var harness = new Harness(useProductionDefaultPort: true);

        harness.FallbackPort.Should().Be(
            AgentConfigDefaults.DefaultUdpPort,
            "生产接线（Program.cs 不传回落端口）拿到的必须就是这个常量 —— "
            + "否则'文档写 8090、实际听在别处'会在现场重演");

        harness.Listener.IsRunning.Should().BeFalse("这条用例只读属性，绝不 Apply（Apply 才会去绑端口）");
    }

    /// <summary>
    /// 显式关掉仍然必须<b>真的不绑端口</b> —— 这是"默认开启"的另一半：
    /// 默认值不能变成"关不掉"。
    ///
    /// <para>⚠️ 这是<b>已连接中心</b>时的语义。没连上中心时它不适用
    /// （那时一律监听）—— 见下面「需求①」那一组用例。</para>
    /// </summary>
    [Fact]
    public async Task An_explicit_disable_still_leaves_no_port_behind()
    {
        await using var harness = new Harness();

        // 先按默认值起来（证明它是开着的），再显式关掉。
        ApplyConnected(harness.Listener, null);
        harness.Listener.IsRunning.Should().BeTrue();

        ApplyConnected(harness.Listener, Config(FreeUdpPort(), enabled: false));

        harness.Listener.IsRunning.Should().BeFalse();
        harness.Listener.Port.Should().BeNull();
        harness.Listener.StateText.Should().Contain("没有启用 UDP 指令通道");
    }

    /// <summary>
    /// <see cref="UdpCommandListener.Stop"/> 是"不管配置怎么写，现在就是不许听"。
    ///
    /// <para>
    /// 🔥 它存在的理由：<b>不能用 <c>Apply(null, …)</c> 来表达"关"</b> ——
    /// 在 2026-10-14 把 null 的语义改成"用默认值（开）"之后，那么写会做出
    /// "不许听的机器照样能被 UDP 关机"这种自相矛盾的行为。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 2026-10-14 起它的场景<b>收窄了</b>：以前"被管理员吊销"也走这条，
    /// 现在吊销属于"没连着中心"⇒ 反而要<b>继续听着</b>（需求①）。
    /// 所以它现在只表达一件事：<b>配置层面明确说了不要听</b>（外加将来的本机策略）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stop_closes_the_channel_even_though_the_config_says_enabled()
    {
        await using var harness = new Harness();

        ApplyConnected(harness.Listener, null);
        harness.Listener.IsRunning.Should().BeTrue();

        harness.Listener.Stop("本机已在配置中停用任务下发（Agent:Tasks:Enabled=false）。");

        harness.Listener.IsRunning.Should().BeFalse();
        harness.Listener.StateText.Should().Contain("停用任务下发");

        // 停着的时候一条指令都不该被执行。
        harness.Power.ShutdownCount.Should().Be(0);
        harness.Power.RestartCount.Should().Be(0);

        // 而且它不会自己复活 —— 复活只能由调用方**显式** Apply 决定（幂等，可以放心再调）。
        ApplyConnected(harness.Listener, null);
        harness.Listener.IsRunning.Should().BeTrue("显式 Apply 才是唯一的重启开关，Stop 不是「永久黑名单」");
    }

    /// <summary>
    /// 🔥 <b>2026-10-13 起通道不再依赖 agentKey</b>（报文不签名了，也就不需要派生通道密钥）。
    /// 这条用例钉住那个变化：<b>没有任何凭据也要能起来</b>。
    /// <para>
    /// 改版前这里是反过来的（"还没注册 ⇒ 起不来，等拿到凭据再补一次 Apply"）——
    /// 那正是"装完机器、配置也启用了，端口却一直不开"的现场形态。
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_channel_starts_even_before_the_machine_is_registered()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        harness.Listener.IsRunning.Should().BeTrue("明文协议不依赖任何凭据 —— 装完就该能用");
        harness.Listener.Port.Should().Be(port);

        // 而且真的收得到（不是"绑上了但没人处理"）。
        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1);
    }

    [Fact]
    public async Task A_port_outside_the_allowed_range_is_refused()
    {
        await using var harness = new Harness();

        ApplyConnected(harness.Listener, Config(80));

        harness.Listener.IsRunning.Should().BeFalse();
        harness.SawLog("超出允许范围").Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // 需求①（2026-10-14）：**没连上中心时，UDP 监听必须开着**
    //
    // 这条通道的定位是"中心网页之外的那条旁路"，而旁路的价值恰恰在**中心不可达**时才体现
    // （展厅断网、中心在维护、机器刚上架还没注册成功）。所以"该不该听"的最终裁决是
    // **此刻是否连着中心**，而不是配置里的 Enabled —— 只有连上中心之后，配置才说了算。
    //
    // 两个包装方法（ApplyConnected / ApplyStandalone）把这件事写进了每一行调用里。
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 本需求最核心的一条：配置里写的是 <c>Enabled=false</c>（中心不让这台机器听），
    /// 但<b>此刻没连着中心</b> ⇒ 必须照听不误，端口沿用配置里那个。
    /// </summary>
    [Fact]
    public async Task Not_connected_keeps_listening_even_though_the_config_disables_udp()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyStandalone(harness.Listener, Config(port, enabled: false));

        harness.Listener.IsRunning.Should().BeTrue(
            "没连上中心时配置里的 Enabled=false 要被忽略 —— 否则中心一断，这台机器就再也没法被关机了。"
            + $"当前状态：{harness.Listener.StateText}");

        harness.Listener.Port.Should().Be(port, "端口要沿用配置里那个：现场的发送脚本指着它");

        harness.Listener.StateText.Should().Contain(
            "未连接中心",
            "状态文案必须说出**为什么**在听 —— 否则现场会以为这是配置的功劳，然后去配置里找一个不存在的开关");

        // 而且真的收得到（不是"绑上了但没人处理"）。
        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1, "强制监听的意义就在于这条指令能真的落地");
    }

    /// <summary>
    /// 未连接时端口越界 ⇒ <b>回落到默认端口继续听</b>，<b>不是</b>拒绝监听。
    ///
    /// <para>已连接时的语义仍是原来那个（越界 = 拒绝监听并说明原因）。两条路径的差别是<b>有意</b>的：
    /// 中心在的时候"配置写错了"该有个人去改；中心不在的时候"配置写错了"不能连累这台机器
    /// 失去最后的控制手段。</para>
    /// </summary>
    [Fact]
    public async Task Not_connected_with_an_out_of_range_port_falls_back_to_the_default()
    {
        await using var harness = new Harness();

        ApplyStandalone(harness.Listener, Config(80, enabled: false));

        harness.Listener.IsRunning.Should().BeTrue("回落继续听，而不是 Stop —— 拒绝监听等于把能力关掉，与需求相反");
        harness.Listener.Port.Should().Be(harness.FallbackPort);

        harness.SawLog("回落到").Should().BeTrue(
            "端口没用上必须留一句 warn，否则现场现象是「我明明配了 80，往 80 发却没反应」");
    }

    /// <summary>
    /// 全新机器（<b>从没连过中心、本机也没有配置</b>）也必须一装好就在听。
    ///
    /// <para>
    /// 🔥 这条配对的是 <c>AgentRuntime.RunAsync</c> 开头那次**无条件**的 Apply。
    /// 改版前 <c>Apply</c> 只在"本机有配置（<c>RestoreFromStore</c> 成功触发 <c>Applied</c>）"
    /// 或"连上中心之后"才会被调用，于是那种新机器 <c>netstat</c> 里连 8090 都看不见 ——
    /// 现场形态就是"机器装好了，发指令毫无反应"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_brand_new_machine_listens_before_it_ever_reaches_the_centre()
    {
        await using var harness = new Harness();

        ApplyStandalone(harness.Listener, null);

        harness.Listener.IsRunning.Should().BeTrue($"装完就该能用 —— 当前状态：{harness.Listener.StateText}");
        harness.Listener.Port.Should().Be(harness.FallbackPort);

        await SendAsync(harness.FallbackPort, "RESTART");
        await WaitUntil(() => harness.Power.RestartCount == 1);

        harness.Power.RestartCount.Should().Be(1);
    }

    /// <summary>
    /// 连上中心之后要**把决定权交回配置**：未连接期间那条强制开着的通道得关掉。
    /// <para>这一条守的是另一半 —— "未连接就听"不能退化成"配了也关不掉"。</para>
    /// </summary>
    [Fact]
    public async Task Connecting_again_hands_the_decision_back_to_the_config()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyStandalone(harness.Listener, Config(port, enabled: false));
        harness.Listener.IsRunning.Should().BeTrue();

        ApplyConnected(harness.Listener, Config(port, enabled: false));

        harness.Listener.IsRunning.Should().BeFalse("中心回来了，配置说关就关");
        harness.Listener.Port.Should().BeNull();
        harness.Listener.StateText.Should().Contain("没有启用 UDP 指令通道");
    }

    /// <summary>
    /// 🔥 只是<b>连接状态</b>变了（端口与黑名单一字未变）⇒ <b>不许重绑端口</b>。
    ///
    /// <para>
    /// 重绑会在那一瞬间留一个没人收包的空窗 —— 而网络抖动就是"断 → 再连"，
    /// 一条 <c>DOWN</c> 正好落在那个空窗里，现场看到的就是"指令没反应"。
    /// 连接状态影响的从来不是"听不听"，只是"为什么在听"，端口本就该一动不动。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Flipping_the_connection_state_does_not_rebind_the_port()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyStandalone(harness.Listener, Config(port));
        var startedLogs = harness.CountLogs("UDP 指令通道已启动");

        harness.Listener.StateText.Should().Contain("未连接中心");

        // 连上 → 断开：端口一次都不该重绑，但状态文案要跟着翻。
        ApplyConnected(harness.Listener, Config(port));
        harness.Listener.StateText.Should().NotContain("未连接中心");
        harness.SawLog("已连上中心").Should().BeTrue("状态怎么变的要有据可查");

        ApplyStandalone(harness.Listener, Config(port));
        harness.Listener.StateText.Should().Contain("未连接中心");
        harness.SawLog("与中心的连接已断开").Should().BeTrue();

        harness.CountLogs("UDP 指令通道已启动").Should().Be(
            startedLogs, "连接状态变了只翻文案，不该重绑端口（重绑 = 一个收包空窗）");

        harness.Listener.Port.Should().Be(port);
        harness.Listener.IsRunning.Should().BeTrue();

        // 而且这条通道从始至终都是活的。
        await SendAsync(port, "RESTART");
        await WaitUntil(() => harness.Power.RestartCount == 1);

        harness.Power.RestartCount.Should().Be(1);
    }

    /// <summary>
    /// <c>Agent:Tasks:Enabled=false</c> 是<b>总开关</b>：连"未连接必须听"这条新规则也压不过它。
    ///
    /// <para>它对应的是"这台机器根本就不该被远程控制"那种现场决定（例如产线上的机台），
    /// 所以它是唯一能压过需求① 的东西 —— 关就要在 <c>netstat</c> 里看不见，
    /// 不能变成"关不掉"。</para>
    /// </summary>
    [Fact]
    public async Task The_master_switch_beats_the_standalone_rule()
    {
        await using var harness = new Harness(tasksEnabled: false);

        ApplyStandalone(harness.Listener, Config(FreeUdpPort(), enabled: true));
        ApplyStandalone(harness.Listener, null);

        harness.Listener.IsRunning.Should().BeFalse("总开关优先于一切，包括「未连接必须开」");
        harness.Listener.Port.Should().BeNull();
        harness.SawLog("停用任务下发").Should().BeTrue("要留下一句可读的原因，否则现场只会看到'端口没开'");
    }

    // ═══════════════════════════════════════════════════════════
    // 第 3 关：明文指令的识别
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_plain_DOWN_shuts_the_machine_down()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        harness.Listener.IsRunning.Should().BeTrue();

        await SendAsync(port, "DOWN");

        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1);
        harness.Power.RestartCount.Should().Be(0);
        harness.Power.LastReason.Should().Contain("UDP 指令", "日志要能回答「这台机器是被谁、用什么指令关掉的」");
    }

    [Fact]
    public async Task A_plain_RESTART_restarts_the_machine()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, "RESTART");

        await WaitUntil(() => harness.Power.RestartCount == 1);

        harness.Power.RestartCount.Should().Be(1);
        harness.Power.ShutdownCount.Should().Be(0, "「重启计算机」绝不能被当成「关机」");
    }

    [Fact]
    public async Task The_legacy_REBOOT_token_still_maps_to_a_restart()
    {
        // 历史文档与老发送脚本里全是 REBOOT —— 改版只把 RESTART 立为正式写法，
        // 并没有把 REBOOT 作废（作废它只会让现场脚本"突然失灵"）。
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, "REBOOT");

        await WaitUntil(() => harness.Power.RestartCount == 1);

        harness.Power.RestartCount.Should().Be(1);
        harness.Power.ShutdownCount.Should().Be(0);
    }

    /// <summary>
    /// 🔥 现场最常用的发送方式是 <c>echo DOWN | nc -u -w1 主机 8090</c> ——
    /// 那会在末尾带一个换行。因为一个换行就把指令丢掉，是最没道理的一种"无效果"。
    /// </summary>
    [Theory]
    [InlineData("down")]
    [InlineData("Down")]
    [InlineData("DOWN\n")]
    [InlineData("  DOWN  ")]
    [InlineData("DOWN\r\n")]
    public async Task Case_and_surrounding_whitespace_do_not_matter(string payload)
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, payload);

        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1);
    }

    /// <summary>
    /// 音量必须转交<b>会话侧</b>：Session 0 里没有音频端点，
    /// 服务进程自己改音量是"执行成功、用户一个音节都听不到"的无声失败。
    /// </summary>
    [Theory]
    [InlineData("+", "up")]
    [InlineData("-", "down")]
    [InlineData("0", "mute")]
    [InlineData("1", "unmute")]
    public async Task The_four_volume_tokens_are_forwarded_to_the_session_side(string token, string expected)
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, token);

        await WaitUntil(() => harness.Session.Count == 1);

        harness.Session.Count.Should().Be(1);
        harness.Session.Requests[0].Method.Should().Be(SessionCommandMethods.Volume);
        harness.Session.Requests[0].Argument.Should().Be(expected);

        // 服务侧绝不能自己去动电源（音量指令不是电源指令）。
        harness.Power.ShutdownCount.Should().Be(0);
        harness.Power.RestartCount.Should().Be(0);
    }

    /// <summary>
    /// 🔥 <b>同一条指令连发几次就执行几次</b> —— 这是<b>刻意</b>的（旧版的 nonce 防重放已删除）。
    /// <para>
    /// 明文报文里没有 nonce，而且"音量 +"本来就是一个<b>可重复的增量操作</b>：
    /// 用重放表去挡它，挡掉的是正常操作（用户连按三次就只 +1）。
    /// 这条用例的存在就是为了防止将来有人"顺手把防重放加回来"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Repeating_the_same_command_executes_it_every_time()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        for (var i = 0; i < 3; i++)
        {
            await SendAsync(port, "+");
            await Task.Delay(50);
        }

        await WaitUntil(() => harness.Session.Count >= 3);

        harness.Session.Count.Should().Be(3, "重复报文是合法的新指令，不该被去重");
    }

    /// <summary>
    /// 认不出的字符串必须被丢弃，而且要留一句可读的原因 ——
    /// "发了个错的指令、机器毫无反应、日志里也没有"是最难查的一类现场问题。
    /// </summary>
    [Theory]
    [InlineData("HELLO")]
    [InlineData("SHUTDOWN")]
    [InlineData("power:foo")]
    [InlineData("volume:")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unrecognised_payload_is_dropped_with_a_reason(string payload)
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, payload);

        await WaitUntil(() => harness.CountLogs("丢弃来自") >= 1);
        await Task.Delay(150);

        harness.SawLog("丢弃来自").Should().BeTrue();
        harness.Power.ShutdownCount.Should().Be(0);
        harness.Power.RestartCount.Should().Be(0);
        harness.Session.Count.Should().Be(0);
    }

    /// <summary>本项目的 <c>name:argument</c> 写法也要能收（中心网页与既有配置文件用它）。</summary>
    [Fact]
    public async Task The_project_style_token_is_also_accepted()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, "power:shutdown");

        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1);
    }

    /// <summary>
    /// 🔥 <b>演练模式</b>（<c>Agent:Tasks:ExecuteUdpCommands=false</c>）：
    /// 黑名单与报文解析<b>全都照常走完</b>（它们才是要验证的东西），
    /// 只是最后一步不真的动手。现场第一次接线时靠它"先看日志不动机器"。
    /// </summary>
    [Fact]
    public async Task Dry_run_parses_the_command_but_does_not_execute_it()
    {
        await using var harness = new Harness(executeUdpCommands: false);
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        await SendAsync(port, "DOWN");

        await WaitUntil(() => harness.SawLog("收到指令"));

        harness.SawLog("收到指令").Should().BeTrue("演练模式下也要留下「收到了什么」的日志，否则演练没有意义");
        harness.SawLog("演练模式").Should().BeTrue();

        // 给它一点时间"万一真执行了"。
        await Task.Delay(200);

        harness.Power.ShutdownCount.Should().Be(0, "演练模式绝不允许真的关机");
    }

    // ═══════════════════════════════════════════════════════════
    // 第 2 关：来源 IP 黑名单
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_blacklisted_source_is_dropped()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        // 本机发出去的报文来源就是回路地址 —— 把它拉黑。
        ApplyConnected(harness.Listener, Config(port, blocked: new[] { "127.0.0.1" }));
        harness.Listener.IsRunning.Should().BeTrue();

        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.SawLog("在黑名单内"));

        harness.SawLog("在黑名单内").Should().BeTrue();
        harness.Power.ShutdownCount.Should().Be(0);

        // 把回路地址从名单里去掉 → 同一条指令立刻生效。
        // （这一步同时证明"上一条没执行"确实是黑名单拦的，而不是 socket 根本没收到。）
        ApplyConnected(harness.Listener, Config(port, blocked: Array.Empty<string>()));

        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1);
    }

    /// <summary>
    /// 名单里有别的 IP 时，<b>不相关来源的指令必须照常执行</b>——
    /// 这是"任意来源皆可"的核心：黑名单只针对被点名的主机。
    /// </summary>
    [Fact]
    public async Task A_source_not_in_the_blacklist_is_still_accepted()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port, blocked: new[] { "10.99.99.99", "192.168.5.5" }));

        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Power.ShutdownCount == 1);

        harness.Power.ShutdownCount.Should().Be(1);
    }

    [Fact]
    public async Task Rejections_are_throttled_so_a_flood_cannot_fill_the_log()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        for (var i = 0; i < 50; i++) await SendAsync(port, "GARBAGE-INSTRUCTION");

        await WaitUntil(() => harness.CountLogs("丢弃来自") >= 1);
        await Task.Delay(150);

        var logged = harness.CountLogs("丢弃来自");

        logged.Should().BeGreaterOrEqualTo(1, "第一条必须记下来，否则等于什么都没发生");
        logged.Should().BeLessThanOrEqualTo(
            2,
            "灌包时不能一条一行 —— 让'防灌包'的实现反过来把客户机磁盘写满是最讽刺的失败");
    }

    // ═══════════════════════════════════════════════════════════
    // 生命周期
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Changing_the_port_rebinds_and_stops_the_old_one()
    {
        await using var harness = new Harness();

        var first = FreeUdpPort();
        var second = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(first));
        harness.Listener.Port.Should().Be(first);

        ApplyConnected(harness.Listener, Config(second));

        harness.Listener.IsRunning.Should().BeTrue();
        harness.Listener.Port.Should().Be(second);

        // 新端口收得到。
        await SendAsync(second, "DOWN");
        await WaitUntil(() => harness.Power.ShutdownCount == 1);
        harness.Power.ShutdownCount.Should().Be(1);

        // 旧端口已经关掉：再发过去不该有任何新动作。
        // （UDP 是无连接的，发出去不会报错 —— 只能靠"没有新执行"来证伪。）
        try
        {
            await SendAsync(first, "DOWN");
        }
        catch (SocketException)
        {
            // Windows 上向已关闭的端口发 UDP 可能借 ICMP 报错，属正常。
        }

        await Task.Delay(200);
        harness.Power.ShutdownCount.Should().Be(1, "换端口后旧监听必须真的停掉");
    }

    [Fact]
    public async Task The_same_configuration_is_not_rebound_on_every_apply()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        var startedLogs = harness.CountLogs("UDP 指令通道已启动");

        // 心跳与版本对齐会反复调 Apply —— 参数一字未变时不该重绑端口。
        for (var i = 0; i < 5; i++) ApplyConnected(harness.Listener, Config(port));

        harness.CountLogs("UDP 指令通道已启动").Should().Be(
            startedLogs, "参数未变时的重复 Apply 是幂等的，否则每次心跳都会抖一下监听");
        harness.Listener.IsRunning.Should().BeTrue();
    }

    /// <summary>
    /// 黑名单变了也算"参数变了"，必须重新绑 —— 否则"加了一个 IP 进去"永远不会生效。
    /// </summary>
    [Fact]
    public async Task Changing_the_blacklist_rebinds_the_channel()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));
        var startedLogs = harness.CountLogs("UDP 指令通道已启动");

        ApplyConnected(harness.Listener, Config(port, blocked: new[] { "10.0.0.1" }));

        harness.CountLogs("UDP 指令通道已启动").Should().BeGreaterThan(
            startedLogs, "黑名单是监听状态的一部分，改了就得重绑");
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_closes_the_channel()
    {
        var harness = new Harness();

        ApplyConnected(harness.Listener, Config(FreeUdpPort()));
        harness.Listener.IsRunning.Should().BeTrue();

        await harness.Listener.DisposeAsync();

        harness.Listener.IsRunning.Should().BeFalse();
        harness.SawLog("随进程停机关闭").Should().BeTrue();

        // 第二次 Dispose 不能抛（它既是 DI 单例、也会被主循环显式收一次）。
        var again = async () => await harness.Listener.DisposeAsync();
        await again.Should().NotThrowAsync();

        await harness.Log.DisposeAsync();
        harness.DeleteDataDirectory();
    }

    // ═══════════════════════════════════════════════════════════
    // 「接收到的数据」（2026-10-13）：每条报文的结论都要留痕
    //
    // 这一组用例守的是"现场能不能自己看出问题在哪"：
    // 光有"执行成功/失败"的日志不够 —— 关键是要能区分
    // **没收到 / 收到了认不出 / 认出了没执行** 这三种长得一模一样的现象。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_recognized_command_is_recorded_as_executed_with_its_source()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Power.ShutdownCount == 1);
        await WaitUntil(() => harness.Received.ReceivedCount > 0);

        var entry = harness.Received
            .Snapshot(harness.Listener.IsRunning, harness.Listener.Port, harness.Listener.StateText, harness.Listener.ExecuteEnabled)
            .Entries!
            .First();

        entry.Kind.Should().Be(AgentReceivedKinds.Udp);
        entry.Raw.Should().Be("DOWN");
        entry.Outcome.Should().Be(AgentReceivedOutcomes.Executed);
        entry.Source.Should().StartWith(
            "127.0.0.1",
            "来源必须记成 IP:端口 —— 现场第一件事就是确认'这条到底是谁发的'");
    }

    [Fact]
    public async Task An_unparseable_datagram_is_recorded_as_unrecognized_verbatim()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        // 现场很常见的一种形态：把**动作名**当成了指令 token（以为要发 "shutdown"）。
        // 报文被丢弃、什么都不做（正确），但记录里必须留着原文 ——
        // 否则现场永远看不出"我发的到底跟文档差在哪"。
        await SendAsync(port, "shutdown");
        await WaitUntil(() => harness.Received.ReceivedCount > 0);

        var entry = harness.Received
            .Snapshot(harness.Listener.IsRunning, harness.Listener.Port, harness.Listener.StateText, harness.Listener.ExecuteEnabled)
            .Entries!
            .First();

        entry.Outcome.Should().Be(AgentReceivedOutcomes.Unrecognized);
        entry.Raw.Should().Be("shutdown", "原文必须原样保留 —— 记录被 normalize 过就再也查不出问题了");
        entry.Detail.Should().NotBeNullOrWhiteSpace("必须带一句人话说明'为什么没认出来'");

        harness.Power.ShutdownCount.Should().Be(0, "认不出的报文绝不能被执行");
    }

    [Fact]
    public async Task Trailing_whitespace_does_not_break_a_valid_command_but_is_still_recorded()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        // `echo DOWN | nc -u` 是现场最常用的发指令方式，它必然带一个换行。
        // 这件事有**两个**正确行为，缺一不可：
        //   ① 指令照常执行（不能因为一个换行就"无效果"）；
        //   ② 记录里留着**原始字节**（含换行）—— 现场要能看见"我发的确实带了换行"。
        await SendAsync(port, "DOWN\n");

        await WaitUntil(() => harness.Power.ShutdownCount == 1);
        await WaitUntil(() => harness.Received.ReceivedCount > 0);

        harness.Power.ShutdownCount.Should().Be(1, "换行不能吃掉指令 —— 那正是改版前'指令无效果'的根因");

        var entry = harness.Received
            .Snapshot(harness.Listener.IsRunning, harness.Listener.Port, harness.Listener.StateText, harness.Listener.ExecuteEnabled)
            .Entries!
            .First();

        entry.Outcome.Should().Be(AgentReceivedOutcomes.Executed);
        entry.Raw.Should().Be("DOWN\n", "解析器可以 trim，**记录不行** —— 记录要的是原文");
    }

    [Fact]
    public async Task A_blacklisted_source_is_recorded_as_blocked()
    {
        await using var harness = new Harness();
        var port = FreeUdpPort();

        // 把环回地址本身拉黑 —— 测试就是从环回发的，这一条必然命中。
        ApplyConnected(harness.Listener, Config(port, blocked: new[] { "127.0.0.1" }));

        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Received.ReceivedCount > 0);

        var entry = harness.Received
            .Snapshot(harness.Listener.IsRunning, harness.Listener.Port, harness.Listener.StateText, harness.Listener.ExecuteEnabled)
            .Entries!
            .First();

        entry.Outcome.Should().Be(
            AgentReceivedOutcomes.Blocked,
            "被拉黑与'认不出'是两件完全不同的事，记录里必须分得开");
        entry.Raw.Should().Be("DOWN", "被拉黑也留着原文 —— 有些黑名单是误配的，得看得见它拦了什么");

        harness.Power.ShutdownCount.Should().Be(0);
    }

    [Fact]
    public async Task Dry_run_records_the_command_as_ignored_rather_than_executed()
    {
        await using var harness = new Harness(executeUdpCommands: false);
        var port = FreeUdpPort();

        ApplyConnected(harness.Listener, Config(port));

        await SendAsync(port, "DOWN");
        await WaitUntil(() => harness.Received.ReceivedCount > 0);

        var entry = harness.Received
            .Snapshot(harness.Listener.IsRunning, harness.Listener.Port, harness.Listener.StateText, harness.Listener.ExecuteEnabled)
            .Entries!
            .First();

        // 🔥 这一条专治"指令收到了、也认出来了，但机器没反应" ——
        //    演练模式开着时就是这个现象，而记录里它必须**不是** executed。
        entry.Outcome.Should().Be(AgentReceivedOutcomes.Ignored);
        harness.Listener.ExecuteEnabled.Should().BeFalse();
        harness.Power.ShutdownCount.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════════════

    private static AgentConfigDto Config(int port, bool enabled = true, string[]? blocked = null) => new(
        Version: 1,
        UpdatedAt: DateTimeOffset.Now,
        Udp: new AgentUdpChannelDto(enabled, port, blocked),
        SendTasks: Array.Empty<AgentSendTaskDto>(),
        ScheduledTasks: Array.Empty<AgentScheduledTaskDto>(),
        LaunchTasks: Array.Empty<AgentLaunchTaskDto>());

    /// <summary>
    /// 「<b>此刻连着中心</b>」时应用一份配置 —— 绝大多数用例的默认情形（配置说了算）。
    /// <para>两个包装方法（本方法 + <see cref="ApplyStandalone"/>）不只是为了少打字：
    /// 它们在每一行调用上都把"当时连着中心没有"这件事写成了显式前提，
    /// 而不是留给读者去猜那个 <c>true</c> 是什么意思。</para>
    /// </summary>
    private static void ApplyConnected(UdpCommandListener listener, AgentConfigDto? config)
        => listener.Apply(config, connected: true);

    /// <summary>「<b>没连着中心</b>」时应用一份配置 —— 需求① 的那条路径（未连接必须监听）。</summary>
    private static void ApplyStandalone(UdpCommandListener listener, AgentConfigDto? config)
        => listener.Apply(config, connected: false);

    /// <summary>绑一个空闲 UDP 端口（绑 0 让系统挑，随即释放）。</summary>
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task SendAsync(int port, string message)
    {
        using var sender = new UdpClient();
        var bytes = Encoding.UTF8.GetBytes(message);

        await sender.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    /// <summary>轮询等待某个条件成立（UDP 是异步的，断言不能立刻做）。</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }

    /// <summary>被测对象的全套依赖（真 UDP socket + 桩电源 + 桩会话通道 + 真 LogService）。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <param name="tasksEnabled">本机总开关 <c>Agent:Tasks:Enabled</c>。</param>
        /// <param name="executeUdpCommands">演练模式开关 <c>Agent:Tasks:ExecuteUdpCommands</c>。</param>
        /// <param name="useProductionDefaultPort">
        /// 🔥 <b>默认 false ＝ 换一个空闲端口当"回落端口"</b>（2026-10-15）。
        ///
        /// <para>
        /// 为什么默认就该换：这个"回落端口"在生产里是 <b>8090</b>，而开发机上随时可能
        /// 装着一个<b>开机自启的真 Agent</b> 正占着它 —— 那样这组用例会稳定报几条红，
        /// 而且红的是"这台机器上装了什么"，不是"代码对不对"。
        /// </para>
        ///
        /// <para>
        /// 置 true ＝ 按<b>生产接线</b>走（不注入，回落端口就是
        /// <see cref="AgentConfigDefaults.DefaultUdpPort"/>）—— 只给下面那条
        /// "默认端口到底是几"的用例用，它<b>只读属性、不 Apply</b>，所以不会真去绑 8090。
        /// </para>
        /// </param>
        public Harness(
            bool tasksEnabled = true,
            bool executeUdpCommands = true,
            bool useProductionDefaultPort = false)
        {
            DataDirectory = new TestDataDirectory(
                Path.Combine(Path.GetTempPath(), "cs-udp-listener-tests", Guid.NewGuid().ToString("N")));

            // flushIntervalMs 设得很大：这些用例只断言内存环形缓冲，不需要落盘。
            Log = new LogService(DataDirectory, capacity: 2000, flushIntervalMs: 600_000);

            var options = new AgentOptions
            {
                Tasks = new AgentTaskOptions
                {
                    Enabled = tasksEnabled,
                    ExecuteUdpCommands = executeUdpCommands,
                },
            };

            Options = options;

            // 🔥 执行器是**共享**的那一层（2026-10-15）：UDP 与中心指令走同一份实现，
            //    所以这里与 AgentRuntime 一样是"拿一个执行器塞进去"，而不是自己实现一份。
            Listener = new UdpCommandListener(
                options,
                Log,
                new AgentCommandExecutor(options, Log, Session, Power),
                Received,
                useProductionDefaultPort ? null : FreeUdpPort());
        }

        public TestDataDirectory DataDirectory { get; }

        public LogService Log { get; }

        /// <summary>构造时用的选项（演练模式等开关由它驱动）。</summary>
        public AgentOptions Options { get; }

        /// <summary>「接收到的数据」环形缓冲（2026-10-13）—— 被测对象往它里面写结论。</summary>
        public ReceivedDataLog Received { get; } = new();

        public StubPowerController Power { get; } = new();

        public StubSessionChannel Session { get; } = new();

        public UdpCommandListener Listener { get; }

        /// <summary>
        /// 本次用例里"配置没写端口 / 端口越界"时**该回落到**的那个端口
        /// （＝ <see cref="UdpCommandListener.FallbackPort"/>，默认是一个当时的空闲端口）。
        ///
        /// <para>
        /// ⚠️ 断言要拿它比，<b>不能写死 8090</b> —— 那正是这组用例过去会红的原因。
        /// 生产那个 8090 由下面 <c>The_default_port_is_the_one_the_field_scripts_aim_at</c> 单独钉住。
        /// </para>
        /// </summary>
        public int FallbackPort => Listener.FallbackPort;

        public bool SawLog(string fragment) => CountLogs(fragment) > 0;

        public int CountLogs(string fragment)
            => Log.Recent(2000).Count(e => e.Message.Contains(fragment, StringComparison.Ordinal));

        public void DeleteDataDirectory()
        {
            try
            {
                if (Directory.Exists(DataDirectory.Root)) Directory.Delete(DataDirectory.Root, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响结论。
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Listener.DisposeAsync();
            await Log.DisposeAsync();
            DeleteDataDirectory();
        }
    }

    /// <summary>
    /// 桩电源控制器。<b>绝不真的关机</b> —— 它的调用计数就是"这条指令到底放行了没有"。
    /// </summary>
    private sealed class StubPowerController : IHostPowerController
    {
        private int _shutdowns;
        private int _restarts;

        public int ShutdownCount => Volatile.Read(ref _shutdowns);

        public int RestartCount => Volatile.Read(ref _restarts);

        public string? LastReason { get; private set; }

        public bool IsAvailable => true;

        public string AvailabilityNote => string.Empty;

        public Task<HostPowerResult> ShutdownAsync(string? reason, CancellationToken cancellationToken = default)
        {
            LastReason = reason;
            Interlocked.Increment(ref _shutdowns);

            return Task.FromResult(HostPowerResult.Success("已安排关机（测试桩）"));
        }

        public Task<HostPowerResult> RestartAsync(string? reason, CancellationToken cancellationToken = default)
        {
            LastReason = reason;
            Interlocked.Increment(ref _restarts);

            return Task.FromResult(HostPowerResult.Success("已安排重启（测试桩）"));
        }
    }

    /// <summary>桩会话通道：记录转发过来的指令，绝不触碰真实音频设备。</summary>
    private sealed class StubSessionChannel : ISessionCommandChannel
    {
        private readonly List<SessionCommandRequest> _requests = new();

        public IReadOnlyList<SessionCommandRequest> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public int Count
        {
            get { lock (_requests) return _requests.Count; }
        }

        public bool IsSupported => true;

        public bool IsAvailable => true;

        public Task<SessionCommandResult> SendAsync(
            SessionCommandRequest request,
            CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);

            return Task.FromResult(SessionCommandResult.Success("音量已调高：50%（未静音）"));
        }
    }
}
