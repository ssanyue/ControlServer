using ControlServer.Platform.Abstractions;
using ControlServer.Platform.Windows;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 会话侧音量控制（M4.5）的<b>真实 CoreAudio 互操作</b>测试。
///
/// <para>
/// 这些用例跑在<b>本机真实音频设备</b>上（不是桩）。值得单独立一个文件的原因是：
/// <see cref="WindowsHostAudioController"/> 里那段 COM 声明是按 <b>vtable 槽位顺序</b>排的，
/// 顺序本身就是契约 —— 少写一个方法会让后面所有方法整体前移一格，
/// 症状是<b>栈被写坏 / 进程直接崩</b>，而<b>不是编译错误</b>。
/// 只有一个"真的调一次"的用例能守住这件事，桩永远守不住。
/// </para>
///
/// <para>
/// ⚠️ <b>这里刻意不做任何写操作</b>（不改音量、不切静音）。
/// 测试跑在开发机上，"静音了开发机"这种副作用的烦人程度远超它带来的信心；
/// 写路径（<c>Mute/Unmute/VolumeStepUp/VolumeStepDown</c>）已由一次性真机探针逐一验过并复位，
/// 细节见 <c>REF-M4-Agent.md</c> §18。这里只钉那些<b>跑了就不该破坏机器</b>的不变量。
/// </para>
/// </summary>
public class WindowsHostAudioControllerTests
{
    /// <summary>
    /// 🔥 <b>本次改动引入的核心不变量</b>：<see cref="IAudioController.IsAvailable"/> 与
    /// <see cref="IAudioController.AvailabilityNote"/> 必须严格互反。
    ///
    /// <para>
    /// 这条断言的价值在于它<b>在哪台机器上都成立</b>：有音频设备的机器上一真一空，
    /// 无声卡 / 虚拟机上一假一非空。它同时钉死了两件容易退化的事：
    /// </para>
    /// <list type="number">
    ///   <item><c>IsAvailable</c> 又退化成只判 <c>OperatingSystem.IsWindows()</c> ——
    ///         那会在无声卡机器上谎报"支持"，上层提示词随之退化成一句空话；</item>
    ///   <item><c>AvailabilityNote</c> 被改成返回一句写死的话 ——
    ///         现场就再也看不到"本机没有可用的音频输出设备"这类可操作的原因了。</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Availability_is_exactly_equivalent_to_an_empty_note()
    {
        var audio = new WindowsHostAudioController();

        var available = audio.IsAvailable;
        var note = audio.AvailabilityNote;

        if (available)
        {
            note.Should().BeEmpty(
                "取到了默认音频端点就必须没有告警词，否则上层会显示一句自相矛盾的提示");
        }
        else
        {
            note.Should().NotBeNullOrWhiteSpace(
                "不可用时必须给出人话原因（无声卡 / 声卡被禁用 / 平台未实现），" +
                "否则现场只会看到\u201c音量指令没反应\u201d而无从下手");

            if (OperatingSystem.IsWindows())
            {
                note.Should().NotContain("当前平台未实现",
                    "Windows 上不允许把\u201c拿不到音频端点\u201d伪装成\u201c平台未实现\u201d —— " +
                    "前者能靠启声卡 / 插耳机解决，后者会让人以为只能换操作系统");
            }
        }
    }

    /// <summary>
    /// <see cref="IAudioController.Read"/> 必须<b>永不抛异常</b>，且读回的值要么是
    /// <see cref="HostAudioState.Unknown"/>、要么落在合理区间。
    ///
    /// <para>
    /// 这里覆盖 vtable 的第 7 号（<c>GetMasterVolumeLevelScalar</c>）与第 13 号（<c>GetMute</c>）——
    /// 若槽位顺序错了，这两行<b>根本走不到断言就会崩掉测试宿主</b>，本身就是一种判据。
    /// </para>
    ///
    /// <para>
    /// 不断言具体数值：开发机的音量是多少都算对，唯一不能接受的是"读出来的数不像个音量"。
    /// 同时也不断言"必须非 Unknown"——无声卡机器上 Unknown 是<b>正确</b>答案，
    /// 不准用 <c>0</c> 或 <c>false</c> 冒充"读到了"。
    /// </para>
    /// </summary>
    [Fact]
    public void Read_returns_a_sane_state_or_unknown()
    {
        var audio = new WindowsHostAudioController();

        // HostAudioState 是引用类型，default 会是 null；
        // 兜底值用 Unknown（而不是 default）——语义对得上，也免得下面解引用时告警。
        var state = HostAudioState.Unknown;

        var act = () => state = audio.Read();

        act.Should().NotThrow("读音量是只读操作，任何失败都应转成 HostAudioState.Unknown 而不是往上抛");

        if (state.VolumePercent is { } percent)
        {
            percent.Should().BeInRange(0, 100, "音量百分比是 0~100 的标量乘以 100 再取整得到的");
        }

        if (audio.IsAvailable)
        {
            state.VolumePercent.Should().NotBeNull(
                "端点是取得到的，那么主音量标量就该读得出来；读到 null 说明互操作声明已经漂了");
        }
    }

    /// <summary>
    /// 枚举里没有的动作必须<b>明确失败</b>，而不是悄悄退化成某个真实动作。
    ///
    /// <para>
    /// 这条守的是 <c>switch</c> 的穷尽分支：如果哪天有人把默认分支写成
    /// "落到 <c>VolumeUp</c>" 之类，一个协议版本错配的指令就会把用户的音量调高，
    /// 而<b>调用方看到的是一次成功</b>。
    /// </para>
    ///
    /// <para>
    /// 之所以放在"真实互操作"文件里：这个分支只有在<b>真的取到了端点</b>之后才会被执行到。
    /// 顺带也验证了它不会把无声音的机器弄崩。
    /// </para>
    /// </summary>
    [Fact]
    public void Unknown_action_is_rejected_instead_of_silently_doing_something()
    {
        var audio = new WindowsHostAudioController();

        HostAudioResult? result = null;

        var act = () => result = audio.Apply((HostAudioAction)999);

        act.Should().NotThrow("未知动作不该把进程拖崩，应该走失败这条路回去");

        result!.Ok.Should().BeFalse(
            "枚举外的动作必须失败；静默当成某个已知动作执行会让调用方误以为成功");

        result.Message.Should().NotBeNullOrWhiteSpace("失败必须带一句能看懂的原因");
    }
}
