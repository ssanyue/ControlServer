using ControlServer.Agent.Tasks;
using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 帧内容指纹（M5.5：软编路径的静止检测靠它）。
///
/// <para>
/// <b>为什么值得单独一批用例</b>：指纹判错的方向是不对称的 ——
/// </para>
/// <list type="bullet">
///   <item><b>误判"变了"</b>：退化成改动前的行为（每帧都发），只是白烧 CPU，画面正常；</item>
///   <item><b>误判"没变"</b>：画面<b>永远停住</b>，用户会以为程序卡死，而且日志全绿。</item>
/// </list>
/// <para>
/// 所以这里重点钉的是后者：只要像素有一个字节不同（尤其是<b>指针那一小块</b>），
/// 就必须判成"变了"。
/// </para>
/// </summary>
public sealed class FrameFingerprintTests
{
    [Fact]
    public void Same_pixels_give_the_same_fingerprint()
    {
        var a = MakeFrame(64, 32);
        var b = MakeFrame(64, 32);

        FrameFingerprint.Of(a).Should().Be(FrameFingerprint.Of(b));
    }

    [Fact]
    public void One_changed_byte_changes_the_fingerprint()
    {
        var a = MakeFrame(64, 32);
        var b = MakeFrame(64, 32);

        // 只改一个字节 —— 场景是一个像素的一小部分（指针尖端）动了。
        b.Buffer[1234] ^= 0x01;

        FrameFingerprint.Of(a).Should().NotBe(FrameFingerprint.Of(b));
    }

    [Fact]
    public void Cursor_moving_on_an_otherwise_static_desktop_is_detected()
    {
        // 静止桌面：整幅同一个底色。
        var before = MakeSolidFrame(320, 200, fill: 0x20);
        var after = MakeSolidFrame(320, 200, fill: 0x20);

        // 指针从 (10,10) 移到 (11,10)：只改一个 4 字节像素。
        var offset = (10 * 320 + 10) * 4;
        after.Buffer[offset] = 0xFF;
        after.Buffer[offset + 1] = 0xFF;
        after.Buffer[offset + 2] = 0xFF;

        var other = (10 * 320 + 11) * 4;
        after.Buffer[other] = 0xFF;
        after.Buffer[other + 1] = 0xFF;
        after.Buffer[other + 2] = 0xFF;

        FrameFingerprint.Of(before).Should().NotBe(
            FrameFingerprint.Of(after),
            "指针移动必须被认成「画面变了」—— 否则软编路径会冻住鼠标，而那是最容易被当成卡死的现象");
    }

    [Fact]
    public void Same_bytes_but_different_geometry_differ()
    {
        // 100×100 与 200×50 的 BGRA 都是 40000 字节。
        // 不把尺寸混进初值，这两张就会被判成"同一帧"，而那一刻正是切分辨率之后该重发的时候。
        var buffer = new byte[100 * 100 * 4];

        var small = new CapturedFrame(100, 100, 100 * 4, buffer, DateTimeOffset.UnixEpoch);
        var wide = new CapturedFrame(200, 50, 200 * 4, buffer, DateTimeOffset.UnixEpoch);

        FrameFingerprint.Of(small).Should().NotBe(
            FrameFingerprint.Of(wide),
            "字节数相同但几何不同不是同一帧");
    }

    [Fact]
    public void Empty_buffer_does_not_throw()
    {
        var frame = new CapturedFrame(0, 0, 0, [], DateTimeOffset.UnixEpoch);

        var act = () => FrameFingerprint.Of(frame);

        act.Should().NotThrow("抓屏偶发给 0×0 时也不该把推流循环带崩");
    }

    [Fact]
    public void Length_not_divisible_by_eight_is_covered()
    {
        // 尾部那几个字节走的是逐字节循环 —— 少写它就等于"最后 7 个字节的变化检测不到"。
        var a = new byte[13];
        var b = new byte[13];

        b[12] = 1;

        FrameFingerprint.Of(1, 13, a).Should().NotBe(FrameFingerprint.Of(1, 13, b));
    }

    // ═══════════════════════════════════════════════════
    // 辅助
    // ═══════════════════════════════════════════════════

    private static CapturedFrame MakeSolidFrame(int width, int height, byte fill)
    {
        var buffer = new byte[width * height * 4];
        Array.Fill(buffer, fill);

        return new CapturedFrame(width, height, width * 4, buffer, DateTimeOffset.UnixEpoch, IsCursorIncluded: true);
    }

    private static CapturedFrame MakeFrame(int width, int height)
    {
        var buffer = new byte[width * height * 4];

        // 用一个确定性的伪随机（不用 Random：用例必须每次给同一份数据）。
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i * 31 % 251);

        return new CapturedFrame(width, height, width * 4, buffer, DateTimeOffset.UnixEpoch);
    }
}
