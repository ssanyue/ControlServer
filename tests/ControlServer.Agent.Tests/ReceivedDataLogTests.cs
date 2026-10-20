using ControlServer.Agent.Diagnostics;
using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 「接收到的数据」环形缓冲（2026-10-13）。
///
/// <para>
/// 它是"下发了指令但机器毫无反应"这条排查链上唯一的现场证据，所以这里的断言都围绕
/// <b>"这份证据会不会骗人"</b>展开：
/// </para>
/// <list type="bullet">
///   <item><b>计数与条目数必须分开</b>：缓冲满了之后条目会被挤掉，而"一共收到多少条"
///         不能跟着缩水 —— 否则现场会得出"这台机器什么都没收到"的错误结论；</item>
///   <item><b>原文要原样留着</b>（含空格）——"多发了一个空格"正是最常见的坑；</item>
///   <item><b>超长报文要截断</b>：不能被一个坏掉的发送方把内存顶爆。</item>
/// </list>
/// </summary>
public sealed class ReceivedDataLogTests
{
    [Fact]
    public void Entries_are_newest_first()
    {
        var log = new ReceivedDataLog();

        log.Add(AgentReceivedKinds.Udp, "10.0.0.1:1", "DOWN", AgentReceivedOutcomes.Executed);
        log.Add(AgentReceivedKinds.Udp, "10.0.0.2:2", "RESTART", AgentReceivedOutcomes.Executed);

        var snapshot = log.Snapshot(udpListening: true, udpPort: 8090, udpStateText: "在听", udpExecuteEnabled: true);

        snapshot.Entries.Should().NotBeNull();
        snapshot.Entries![0].Raw.Should().Be("RESTART", "最新的必须在最前面 —— 排查看的就是'刚刚那一下'");
        snapshot.Entries[1].Raw.Should().Be("DOWN");
    }

    [Fact]
    public void Ring_buffer_caps_entries_but_never_caps_the_counter()
    {
        var log = new ReceivedDataLog();

        const int extra = 25;
        var total = ReceivedDataLog.Capacity + extra;

        for (var i = 0; i < total; i++)
        {
            log.Add(AgentReceivedKinds.Udp, "10.0.0.9:9", $"msg-{i}", AgentReceivedOutcomes.Unrecognized);
        }

        log.ReceivedCount.Should().Be(total, "计数是'一共收到多少条'，与缓冲还剩多少无关");

        var snapshot = log.Snapshot(true, 8090, "在听", true);

        snapshot.Entries!.Count.Should().Be(
            ReceivedDataLog.Capacity,
            "环形缓冲只留最近 Capacity 条 —— 多出来的必须丢最旧的，而不是无限长");

        snapshot.ReceivedCount.Should().Be(
            total,
            "条目数会缩水、计数不能缩水 —— 两者混为一个，现场就会把「缓冲挤满了」读成「这台机器没收到东西」");
    }

    [Fact]
    public void CountOnly_increments_the_counter_without_burning_a_slot()
    {
        var log = new ReceivedDataLog();

        for (var i = 0; i < ReceivedDataLog.Capacity + 10; i++) log.CountOnly();

        log.ReceivedCount.Should().Be(ReceivedDataLog.Capacity + 10);

        var snapshot = log.Snapshot(true, 8090, "在听", true);

        snapshot.Entries.Should().BeEmpty("只计数不留条目 —— 丢弃路径不该把有用的历史挤出去");
    }

    [Fact]
    public void Raw_text_is_kept_verbatim_including_trailing_spaces()
    {
        var log = new ReceivedDataLog();

        // 🔥 这就是现场"我明明发了 DOWN"最真实的样子：多发了一个空格。
        //    报文进不来是解析器的事，但**记录必须原样留着** ——
        //    记录里被 trim 掉的话，这个空格就再也查不出来了。
        log.Add(AgentReceivedKinds.Udp, "10.0.0.5:5555", "DOWN ", AgentReceivedOutcomes.Unrecognized, "报文无法识别");

        var entry = log.Snapshot(true, 8090, "在听", true).Entries![0];

        entry.Raw.Should().Be("DOWN ", "原文必须原样保留（含尾随空格）");
        entry.Source.Should().Be("10.0.0.5:5555");
        entry.Outcome.Should().Be(AgentReceivedOutcomes.Unrecognized);
        entry.Detail.Should().Be("报文无法识别");
    }

    [Fact]
    public void Absurdly_long_payloads_are_truncated()
    {
        var log = new ReceivedDataLog();

        var huge = new string('X', ReceivedDataLog.RawMaxLength * 20);

        log.Add(AgentReceivedKinds.Udp, "10.0.0.6:6666", huge, AgentReceivedOutcomes.Unrecognized);

        var entry = log.Snapshot(true, 8090, "在听", true).Entries![0];

        entry.Raw.Length.Should().BeLessThan(
            huge.Length,
            "超长报文必须截断：UDP 单包能塞 64 KB，不截断就是一排自伤式的内存炸弹");

        entry.Raw.Should().StartWith("XXX", "截断也要保留开头 —— 那一段才是判断'这是什么东西'的依据");
    }

    [Fact]
    public void Snapshot_carries_the_channel_state_it_was_given()
    {
        var log = new ReceivedDataLog();

        // 🔥 通道状态是"一条都没收到"时唯一能解释原因的东西 ——
        //    空的列表配上"未监听 + 为什么"，才是一句能读懂的话。
        var snapshot = log.Snapshot(
            udpListening: false,
            udpPort: null,
            udpStateText: "中心下发的配置没有启用 UDP 指令通道。",
            udpExecuteEnabled: false);

        snapshot.Ok.Should().BeTrue();
        snapshot.Status.Should().Be(AgentReceivedStatuses.Ok);
        snapshot.UdpListening.Should().BeFalse();
        snapshot.UdpPort.Should().BeNull();
        snapshot.UdpStateText.Should().Be("中心下发的配置没有启用 UDP 指令通道。");
        snapshot.UdpExecuteEnabled.Should().BeFalse("演练模式这一项直接解释了'收到了却没执行'");
        snapshot.StartedAt.Should().NotBeNull("没有启动时刻就无法判断'这段时间里到底收没收过'");
    }

    [Fact]
    public void Fail_helpers_carry_a_human_readable_reason()
    {
        var offline = AgentReceivedDataDto.Fail(AgentReceivedStatuses.Offline, "这台客户机当前不在线。");

        offline.Ok.Should().BeFalse();
        offline.Status.Should().Be(AgentReceivedStatuses.Offline);
        offline.Message.Should().Be("这台客户机当前不在线。");

        var unsupported = AgentReceivedDataDto.Unsupported("客户端太旧。");

        unsupported.Ok.Should().BeFalse();
        unsupported.Status.Should().Be(AgentReceivedStatuses.Unsupported);
    }
}
