using ControlServer.Contracts;
using ControlServer.Platform.Linux;

namespace ControlServer.Agent.Tests;

/// <summary>
/// Linux 平台层里那几个<b>纯解析函数</b>（M7.2）。
///
/// <para>
/// 🔥 <b>为什么只测这些、不测整个实现</b>：真正读 <c>/sys</c> 与跑 <c>wpctl</c> /
/// <c>last</c> 的部分只能在 Linux 机器上验（会话模式、权限、有没有 systemd 全都是环境事实）。
/// 而"把命令输出解析成数字"这部分<b>可以在任何机器上跑</b>，而且它是这一层里最容易错、
/// 错了又最不容易被发现的：错的表现是"音量显示 1%"、"CPU 温度 48℃ 但风扇狂转" ——
/// 一个看着完全正常的错值，现场不会去怀疑解析。
/// </para>
/// </summary>
public sealed class LinuxPlatformParsersTests
{
    // ═══════════════════════════════════════════════════════════
    // 温度：sysfs 的毫摄氏度
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("45000", 45.0)]
    [InlineData("61500", 61.5)]
    [InlineData("-5000", -5.0)]
    [InlineData(" 72000\n", 72.0)]
    public void MilliCelsius_is_divided_by_1000(string raw, double expected)
    {
        Assert.True(LinuxThermalSource.TryParseMilliCelsius(raw, out var celsius));
        Assert.Equal(expected, celsius, 3);
    }

    [Theory]
    [InlineData("0")]        // 驱动没填 —— 数值合法但**不是读数**
    [InlineData("999999")]   // 传感器故障
    [InlineData("-273000")]  // 绝对零度：明显是没读到
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void Implausible_celsius_is_not_a_reading(string? raw)
    {
        Assert.False(LinuxThermalSource.TryParseMilliCelsius(raw, out _));
    }

    // ⚠️ 这里比对的是**枚举的名字**（字符串），不是枚举值本身：
    //    `SensorKind` 是 internal 的，把它写进 public 测试方法的签名会触发 CS0051
    //    （可访问性不一致）。名字比对同样能把"归错类"钉死，而且读起来更直观。
    [Theory]
    [InlineData("x86_pkg_temp", "Cpu")]
    [InlineData("coretemp", "Cpu")]
    [InlineData("k10temp", "Cpu")]
    [InlineData("cpu-thermal", "Cpu")]
    [InlineData("amdgpu", "Gpu")]
    [InlineData("i915", "Gpu")]
    [InlineData("nvme", "Storage")]
    [InlineData("drivetemp", "Storage")]
    // 🔥 主板温度**不是** CPU：它经常恒为 27℃，混进 CPU 那一格就是"CPU 永远不热"
    [InlineData("acpitz", "Other")]
    [InlineData("BAT0", "Other")]
    [InlineData("", "Other")]
    [InlineData(null, "Other")]
    public void Sensor_type_is_classified(string? name, string expected)
    {
        Assert.Equal(expected, LinuxThermalSource.Classify(name).ToString());
    }

    // ═══════════════════════════════════════════════════════════
    // 音量：三种后端的输出
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Wpctl_volume_is_a_fraction_not_a_percent()
    {
        Assert.True(LinuxAudioController.TryParseWpctl("Volume: 0.48\n", out var percent, out var muted));
        Assert.Equal(48, percent);
        Assert.False(muted);

        // ⚠️ 把 0.48 当百分数用会得到 0% —— 这条断言就是防这个。
        Assert.True(LinuxAudioController.TryParseWpctl("Volume: 1.00 [MUTED]", out var full, out var isMuted));
        Assert.Equal(100, full);
        Assert.True(isMuted);
    }

    [Fact]
    public void Pactl_volume_takes_the_first_channel_percent()
    {
        const string output =
            "Volume: front-left: 65536 / 100% / 0.00 dB,   front-right: 65536 / 100% / 0.00 dB\n"
            + "        balance 0.00\n";

        Assert.True(LinuxAudioController.TryParsePactlVolume(output, out var percent));
        Assert.Equal(100, percent);

        Assert.True(LinuxAudioController.TryParsePactlVolume("Volume: mono: 32768 /  50% / -18.06 dB", out var half));
        Assert.Equal(50, half);
    }

    [Theory]
    [InlineData("Mute: yes", true)]
    [InlineData("Mute: no", false)]
    public void Pactl_mute_is_yes_or_no(string output, bool expected)
    {
        Assert.True(LinuxAudioController.TryParsePactlMute(output, out var muted));
        Assert.Equal(expected, muted);
    }

    [Theory]
    [InlineData("  Front Left: Playback 51 [75%] [-15.00dB] [on]", 75, false)]
    [InlineData("  Front Left: Playback 0 [0%] [-51.00dB] [off]", 0, true)]
    public void Amixer_parses_percent_and_switch(string output, int expectedPercent, bool expectedMuted)
    {
        Assert.True(LinuxAudioController.TryParseAmixer(output, out var percent, out var muted));
        Assert.Equal(expectedPercent, percent);
        Assert.Equal(expectedMuted, muted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("amixer: Unable to find simple control 'Master',0")]
    [InlineData(null)]
    public void Garbage_audio_output_is_not_a_reading(string? output)
    {
        Assert.False(LinuxAudioController.TryParseWpctl(output, out _, out _));
        Assert.False(LinuxAudioController.TryParsePactlVolume(output, out _));
        Assert.False(LinuxAudioController.TryParsePactlMute(output, out _));
        Assert.False(LinuxAudioController.TryParseAmixer(output, out _, out _));
    }

    // ═══════════════════════════════════════════════════════════
    // 开关机记录：last -x 的行
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Boot_line_is_parsed_with_its_iso_time()
    {
        const string line =
            "reboot   system boot  6.8.0-45-generic 2026-10-15T21:28:03+08:00   still running";

        Assert.True(LinuxPowerEventSource.TryParseLastLine(line, out var reading));
        Assert.Equal(AgentPowerEventKinds.Boot, reading.Kind);
        Assert.Equal(2026, reading.At.Year);
        Assert.Equal(21, reading.At.Hour);
        Assert.Equal(28, reading.At.Minute);
        Assert.Equal(reading.At.ToUnixTimeSeconds(), reading.RecordId);
    }

    [Fact]
    public void Shutdown_and_reboot_are_told_apart_by_the_first_column()
    {
        const string shutdown =
            "shutdown system down  6.8.0-45-generic 2026-10-15T21:30:03+08:00 - 2026-10-15T21:30:05+08:00  (00:00)";
        const string reboot =
            "reboot   system down  6.8.0-45-generic 2026-10-15T22:10:00+08:00 - 2026-10-15T22:12:00+08:00  (00:02)";

        Assert.True(LinuxPowerEventSource.TryParseLastLine(shutdown, out var a));
        Assert.Equal(AgentPowerEventKinds.Shutdown, a.Kind);

        Assert.True(LinuxPowerEventSource.TryParseLastLine(reboot, out var b));
        Assert.Equal(AgentPowerEventKinds.Restart, b.Kind);

        // 两个时刻都是**第一个** ISO 时间（后一个是记录结束时间，与本次事件无关）。
        Assert.Equal(21, a.At.Hour);
        Assert.Equal(22, b.At.Hour);
    }

    [Fact]
    public void Crash_is_the_only_way_to_see_an_unexpected_power_loss()
    {
        const string line =
            "crash    system crash 6.8.0-45-generic 2026-10-15T09:00:00+08:00 - 2026-10-15T21:28:03+08:00  (12:28)";

        Assert.True(LinuxPowerEventSource.TryParseLastLine(line, out var reading));
        Assert.Equal(AgentPowerEventKinds.Unexpected, reading.Kind);
    }

    [Theory]
    // 🔴 runlevel 记录**不是**开关机：混进来会让每一行都变成两条事件
    [InlineData("runlevel (to lvl 5)  6.8.0-45-generic 2026-10-15T21:28:05+08:00 - 2026-10-15T21:30:03+08:00  (00:01)")]
    [InlineData("wtmp begins Wed Oct 15 21:00:00 2026")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_power_lines_are_skipped(string? line)
    {
        Assert.False(LinuxPowerEventSource.TryParseLastLine(line, out _));
    }
}
