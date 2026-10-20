using System.Reflection;
using ControlServer.Contracts;
using ControlServer.Server.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>Hub 方法签名的硬约束</b>：参数个数必须与调用方的实参个数<b>严格一致</b>。
///
/// <para>
/// 实测结论（<c>.tools/hub-arity-probe</c>，SignalR 8.0.10，四种组合都跑过）：
/// </para>
/// <table>
///   <tr><th>调用方实参</th><th>服务端方法参数</th><th>结果</th></tr>
///   <tr><td>1</td><td>2（第 2 个带 C# 默认值）</td><td><b>失败</b></td></tr>
///   <tr><td>2</td><td>2</td><td>成功</td></tr>
///   <tr><td>2</td><td>1</td><td><b>失败</b></td></tr>
///   <tr><td>1</td><td>1</td><td>成功</td></tr>
/// </table>
///
/// <para>
/// ⇔ <b>C# 的默认值参数在 SignalR 里完全不起作用</b>。这条反直觉，而它的代价极大：
/// 一次"顺手给心跳加个可选参数"的改动，会让还在跑旧版的客户机<b>整条心跳失败</b> ——
/// 现场表现是"几十台机器的在线状态一起变灰"，而它们其实一切正常，
/// 排查会一路查向网络，离真正的原因（一次源码改动）十万八千里。
/// </para>
///
/// <para>
/// 所以这条纪律不能只写在注释里 —— 它在编译期<b>不会被任何东西挡住</b>。这组用例用反射钉住它。
/// </para>
/// </summary>
public sealed class HubContractArityTests
{
    /// <summary>本解决方案里全部 Hub 类型（自动发现，新增 Hub 也一并受约束）。</summary>
    private static IEnumerable<Type> HubTypes()
        => typeof(AgentHub).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(Hub).IsAssignableFrom(t));

    private static IEnumerable<MethodInfo> HubMethods()
        => HubTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            // Hub 基类自己的方法（OnConnectedAsync 等）不是协议方法，跳过。
            .Where(m => m.DeclaringType is not null && m.DeclaringType != typeof(Hub));

    [Fact]
    public void No_hub_method_declares_a_parameter_with_a_default_value()
    {
        var offenders = HubMethods()
            .Where(m => m.GetParameters().Any(p => p.HasDefaultValue || p.IsOptional))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}"
                + $"({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
            .ToArray();

        offenders.Should().BeEmpty(
            "SignalR 对**少传**的实参不会使用 C# 默认值，而是直接判调用失败；"
            + "所以任何带默认值的 Hub 参数都是一个「看起来能用、实际让旧客户端整体失效」的陷阱");
    }

    [Fact]
    public void The_heartbeat_keeps_exactly_one_parameter()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.Heartbeat));

        method.Should().NotBeNull("心跳是客户机与服务端之间最老、最不能断的那条消息");

        method!.GetParameters().Should().HaveCount(
            1,
            "🔥 心跳的参数个数**必须**是 1：多一个（哪怕带默认值）就会让旧客户端的心跳整条失败，"
            + "表现成几十台机器一起变灰。要新增状态请另开一个 Hub 方法（如 ReportUdpChannel）");
    }

    /// <summary>
    /// UDP 通道状态必须走<b>独立的消息</b>—— 这条用例顺便守住"它还在"。
    /// </summary>
    [Fact]
    public void The_udp_channel_state_has_its_own_message()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.ReportUdpChannel));

        method.Should().NotBeNull();
        method!.GetParameters().Should().HaveCount(1);
        method.ReturnType.Should().Be<ControlServer.Contracts.AgentIngestAckDto>(
            "客户端要能知道「中心收到没有」，否则无法区分「中心不认识这个方法（该忽略）」与「网络断了（该重试）」");
    }

    /// <summary>
    /// 编码能力上报（M5.0 第二批）同样必须<b>独立成一条消息</b>。
    ///
    /// <para>
    /// 🔥 这一条尤其要紧：它是 M5 全部自适应决策（编码格式、分帧编码、8K 目标）
    /// 的唯一输入。一旦有人图省事把它塞进心跳或 Welcome，
    /// 代价不是"这条消息坏了"，而是<b>旧客户端的心跳整条失败</b>
    /// （几十台机器的在线状态一起变灰），而它们其实一切正常。
    /// </para>
    /// </summary>
    [Fact]
    public void The_encoder_capability_report_has_its_own_message()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.ReportEncoderCapability));

        method.Should().NotBeNull();
        method!.GetParameters().Should().HaveCount(1);
        method.ReturnType.Should().Be<ControlServer.Contracts.AgentIngestAckDto>(
            "与其它上报同形：客户端要能区分「中心不认识这个方法（该忽略）」与「网络断了（该重试）」");
    }

    /// <summary>
    /// 推帧（M5.1）必须<b>独立成一条消息</b>，而且必须<b>返回应答</b>。
    ///
    /// <para>
    /// 🔥 这一条比其它上报更要紧，理由与它们<b>相反</b>：
    /// 别的上报"失败了忽略掉"顶多丢几条数据；而推帧的应答是被控端<b>唯一的刹车信号</b> ——
    /// 会话被关掉之后，中心回 <c>Rejected</c>，被控端据此当场停掉这路流。
    /// 如果没有返回值（或调用方拿不到它），那台机器会一直抓屏、一直编码、
    /// 一直把帧推进一条没人要的管子，而中心那边什么都看不到（连"它在空转"都看不到）。
    /// </para>
    /// </summary>
    [Fact]
    public void The_remote_frame_push_has_its_own_message()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.PushRemoteFrame));

        method.Should().NotBeNull("M5.1 的码流靠它回中心；名字在 AgentHubClientMethods.PushRemoteFrame");
        method!.GetParameters().Should().HaveCount(
            1,
            "🔥 参数个数必须严格等于调用方实参个数：多一个（哪怕带默认值）都会让推帧整条失败");
        method.ReturnType.Should().Be<ControlServer.Contracts.AgentIngestAckDto>(
            "被控端要靠这个应答才知道该不该停下这路流 —— 它不是一个可有可无的返回值");
    }

    /// <summary>
    /// 推音频（M5.3）必须<b>是独立的一条消息</b>，而且和推帧一样必须<b>返回应答</b>。
    ///
    /// <para>
    /// 🔥 它<b>不能</b>挂在 <see cref="AgentHub.PushRemoteFrame"/> 上、靠载荷里的字段分叉。
    /// 两条流的"忙"判据是<b>相反</b>的：视频忙了就该丢（画面只关心最新），
    /// 音频忙了该<b>等</b>（丢一段就是一个能听见的断点）。
    /// 合成一条消息，就等于把这两套相反的策略写进同一个 <c>switch</c>，
    /// 而写错的那一边<b>不会报错</b> —— 表现只是"声音偶尔咔一下"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不 async、不 await</b>（与推帧同形）：推帧/推音频的调用方是
    /// 被控端的采集循环，它是"发出去就继续"的，等待中心回执会把它拖成串行。
    /// 所以反射拿到的返回类型是 <see cref="AgentIngestAckDto"/> 本身，不是 <c>Task&lt;…&gt;</c>。
    /// </para>
    /// </summary>
    [Fact]
    public void The_remote_audio_push_has_its_own_message()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.PushRemoteAudio));

        method.Should().NotBeNull("M5.3 的声音靠它回中心；名字在 AgentHubClientMethods.PushRemoteAudio");
        method!.GetParameters().Should().HaveCount(
            1,
            "🔥 参数个数必须严格等于调用方实参个数：多一个（哪怕带默认值）都会让推音频整条失败，"
            + "而现场表现只是「没有声音」—— 与「被控机没有声卡」长得一模一样");
        method.ReturnType.Should().Be<AgentIngestAckDto>(
            "与推帧同形：被控端要靠这个应答才知道会话是不是已经被关掉了（该不该停掉这路音频）");
    }

    /// <summary>
    /// 推一帧的<b>一片</b>（M5.4-c① 的分片）必须<b>是独立的一条消息</b>，
    /// 而且参数个数与 <see cref="AgentHub.PushRemoteFrame"/> 一样严格是 1。
    ///
    /// <para>
    /// 🔥 <b>它为什么不能做成"给 PushRemoteFrame 加个参数"</b>：
    /// SignalR 的实参个数必须与签名严格一致（见 <see cref="AgentHub.Heartbeat"/> 的实测备注）
    /// —— 加参数等于让<b>旧中心上所有新客户端的每一帧</b>调用失败。
    /// 分片是"<b>多一条消息</b>"，不是"改一条消息"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 返回类型必须与 <c>PushRemoteFrame</c> 同形（而不是 <c>void</c>）：
    /// 被控端要靠这个应答才知道（a）会话是不是已经被关掉了（该停这路流）；
    /// （b）中心刚才丢了一帧、要不要补关键帧。
    /// </para>
    /// </summary>
    [Fact]
    public void The_remote_frame_chunk_push_has_its_own_message()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.PushRemoteFrameChunk));

        method.Should().NotBeNull(
            "M5.4-c① 的分片靠它回中心；名字在 AgentHubClientMethods.PushRemoteFrameChunk");

        method!.GetParameters().Should().HaveCount(
            1,
            "🔥 参数个数必须严格等于调用方实参个数：多一个（哪怕带默认值）都会让分片整条失败 —— "
            + "而失败的表现是「8K 帧发不出去」，与「这一帧本来就超限」长得一模一样");

        method.ReturnType.Should().Be<AgentIngestAckDto>(
            "分片的应答与整帧那个同形：被控端要靠它区分「会话没了（停这路流）」与「中心丢了一帧（补关键帧）」");

        AgentHubClientMethods.PushRemoteFrameChunk.Should().Be(
            "PushRemoteFrameChunk",
            "两端的名字必须逐字一致 —— 改一个字母，老客户端会安静地忽略这条消息，"
            + "而现场表现只是「超大帧那一路上什么都没发生」");
    }

    /// <summary>
    /// 会话状态上报（M5.1）与推帧<b>是两条消息，不能合并</b>。
    ///
    /// <para>
    /// 🔥 两者的频率差了两个数量级（一帧几十 KB × 每秒十几帧，vs 一路会话总共几条），
    /// 合并之后"状态"这种<b>必须可靠送达</b>的东西会跟着码流的失败一起丢。
    /// 而被控端"起了但没起来"时<b>一帧都不会有</b> —— 那种情况下
    /// 状态上报是中心唯一的判据，丢了它页面就只能永远停在"连接中"。
    /// </para>
    /// </summary>
    [Fact]
    public void The_remote_session_state_report_has_its_own_message()
    {
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.ReportRemoteSession));

        method.Should().NotBeNull();
        method!.GetParameters().Should().HaveCount(1);
        method.ReturnType.Should().Be<ControlServer.Contracts.AgentIngestAckDto>();
    }

    /// <summary>
    /// 观看端那条通道（<c>/hubs/remote</c>）的两条消息必须存在且参数个数正确。
    ///
    /// <para>
    /// ⚠️ 这次的约束方向<b>反过来</b>：<c>Join</c>/<c>Leave</c> 是<b>浏览器调服务端</b>，
    /// 所以"实参个数"由我们自己的前端决定 —— 但它一旦与后端不一致，
    /// 表现与客户机那边一模一样（"点了没反应、两边日志都正常"）。
    /// 前端是 TS、后端是 C#，编译器管不到跨语言那一跳，只能靠这条用例钉住。
    /// </para>
    /// </summary>
    [Fact]
    public void The_remote_hub_join_and_leave_take_exactly_one_parameter()
    {
        var join = typeof(RemoteHub).GetMethod(nameof(RemoteHub.Join));
        var leave = typeof(RemoteHub).GetMethod(nameof(RemoteHub.Leave));

        join.Should().NotBeNull("观看端要先入组才会收到帧（不做全站广播）");
        leave.Should().NotBeNull("同一条连接切换会话时必须能退出上一路，否则它会一直收着上一路的高频帧");

        join!.GetParameters().Should().HaveCount(1);
        leave!.GetParameters().Should().HaveCount(1);
    }

    /// <summary>
    /// 🔥 <b>M5.2 的两条输入方法，实参个数必须与前端<b>真正传的</b>一样。</b>
    ///
    /// <para>
    /// 约束方向与上面那条相同（浏览器调服务端，由我们自己的前端决定实参数），
    /// 而这里的后果比 <c>Join</c>/<c>Leave</c> 更重：输入是唯一一类
    /// <b>"发了没反应就等于没发"</b>的动作 —— 个数对不上时 SignalR 会判调用失败，
    /// 而画面上什么都没发生，用户只会说"点不动"，两边日志都很干净。
    /// </para>
    ///
    /// <para>前端那两行的原样（见 <c>RemoteDesktopPanel.vue</c> 的 <c>toggleInput</c> / <c>flushInput</c>）：</para>
    /// <list type="bullet">
    ///   <item><c>invoke('SendInput', { sessionId, events })</c> —— <b>1 个</b>实参（一个 DTO）；</item>
    ///   <item><c>invoke('SetInputEnabled', sessionId, enabled)</c> —— <b>2 个</b>实参。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_remote_hub_input_methods_match_what_the_frontend_passes()
    {
        var sendInput = typeof(RemoteHub).GetMethod(nameof(RemoteHub.SendInput));
        var setInputEnabled = typeof(RemoteHub).GetMethod(nameof(RemoteHub.SetInputEnabled));

        sendInput.Should().NotBeNull("键鼠回传靠它进屋（前端 invoke('SendInput', { sessionId, events })）");
        setInputEnabled.Should().NotBeNull(
            "控制开关必须是**独立的调用**：夹在事件流里的话，它就回答不了「现在是开还是关」");

        sendInput!.GetParameters().Should().HaveCount(
            1,
            "前端一次传一个批次对象；多一个参数（哪怕带默认值）都会让这一整条路静默失效");

        // ⚠️ 断言的是**完整的返回类型**（`Task<T>`）而不是 `T`：
        //    方法是 async，反射拿到的是外层那个 Task —— 打成 `Be<RemoteInputAckDto>()`
        //    会失败，而失败信息看起来像"返回值类型不对"，其实是测试自己写错了。
        sendInput.ReturnType.Should().Be(
            typeof(Task<RemoteInputAckDto>),
            "输入是唯一一类「发了没反应就等于没发」的动作 —— 没有回执就分不清"
            + "「没送到」「被拒了」「那个键在那台机器上本来就没用」");

        setInputEnabled!.GetParameters().Should().HaveCount(
            2,
            "前端传 (sessionId, enabled) 两个实参，严格相等");

        // 🔥 返回类型也是合同的一部分，而它**刚刚改过一次**：
        //    原来返回 RemoteSessionDto?（一个 null 同时说"会话没了"与"你不在看这一路"），
        //    现在返回回执。
        //
        //    ⚠️ 前端用的是 `invoke<T>`，而那个 T 是**断言式的**泛型参数 ——
        //    忘了跟着改，TypeScript 编译器**不会**报错。症状是：每次翻开关都弹一句"被拒"
        //    （字段名对不上 ⇒ `ack.ok` 读成 undefined），而服务端日志显示一切正常。
        setInputEnabled.ReturnType.Should().Be(
            typeof(Task<RemoteInputAckDto>),
            "开关与输入本身是同一类动作 —— 被拒必须能被说出来，而一个 null 说不了两种拒绝");
    }

    /// <summary>
    /// 🔥 <b>把 M5.2 的三条线上名字<b>写死</b>在这里。</b>
    ///
    /// <para>
    /// 这么做看起来是"重复了一遍常量"，但它是<b>故意的</b>：这些字符串是
    /// <b>跨进程、跨语言</b>（C# ↔ TypeScript，以及 C# ↔ 客户机）的合同 ——
    /// 改一个字母就等于换协议，而两边<b>都不会有任何编译错误</b>。
    /// 老客户端遇到不认识的方法名会<b>安静地忽略</b>，
    /// 于是"改了名"这件事在现场表现为"新功能莫名其妙不工作"。
    /// </para>
    ///
    /// <para>
    /// 重复一遍字面量，是唯一能让"改名"这件事<b>必须过一次测试</b>的办法 ——
    /// 改名的人会看到这条失败，然后被迫想清楚兼容性。
    /// </para>
    /// </summary>
    [Fact]
    public void The_m5_2_wire_names_are_pinned()
    {
        AgentHubMethods.RemoteInput.Should().Be(
            "RemoteInput",
            "中心 → 客户机的输入推送；被控端用这个名字注册处理器");

        RemoteHubClientMethods.SendInput.Should().Be(
            "SendInput",
            "浏览器 → 中心的输入回传；前端 invoke 用的就是它");

        RemoteHubClientMethods.SetInputEnabled.Should().Be(
            "SetInputEnabled",
            "浏览器 → 中心的控制开关");
    }

    /// <summary>
    /// 🔥 <b>把 M5.3 的线上名字也写死在这里。</b>
    ///
    /// <para>
    /// 与上一条同一个理由，而这一批多了一层：<see cref="RemoteHubMethods.Audio"/> 是
    /// <b>中心 → 浏览器</b>的消息，两侧一个是 C#、一个是 TypeScript，
    /// 改一个字母两边都<b>不会报错</b> —— 而且失败方式特别安静：
    /// 前端给一个不存在的消息名注册处理器时 SignalR <b>不报错</b>，
    /// 页面上只是"永远没有声音"，而画面一切正常。
    /// </para>
    /// </summary>
    [Fact]
    public void The_m5_3_wire_names_are_pinned()
    {
        AgentHubClientMethods.PushRemoteAudio.Should().Be(
            "PushRemoteAudio",
            "中心 ↔ 客户机：被控端推音频上来用的就是它（名字变了老客户机会安静地忽略）");

        RemoteHubMethods.Audio.Should().Be(
            "Audio",
            "中心 → 浏览器的一段音频；前端按这个名字注册处理器，写错了不会报错、只是永远没声音");

        // 顺带把 M5.1 那两条也钉住 —— 音频是"与画面并列的另一条流"，
        // 两边的名字必须能一眼看出是两个独立的通道。
        RemoteHubMethods.Frame.Should().Be("Frame");
        RemoteHubMethods.SessionChanged.Should().Be("SessionChanged");
    }
}
