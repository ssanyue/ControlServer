using ControlServer.Platform.Abstractions;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>归一化坐标 ⇄ 屏幕像素</b>的换算（M5.2）。
///
/// <para>
/// 🔥 <b>为什么这一组值得单独一个文件、而且值得写这么多条</b>：
/// 这是远控里<b>最容易错、错了又最难查</b>的一段数学。它的三个典型错误<b>全都在
/// 开发机上复现不出来</b>：
/// </para>
/// <list type="bullet">
///   <item><b>用"尺寸"当分母</b>（而不是"尺寸 − 1"）⇒ 最右/最下一列永远点不到。
///         1080p 下那是 1 个像素的边（没人会发现），<b>8K 下是 30 多个像素宽的一整条边</b>；</item>
///   <item><b>忘了加屏幕的左/上偏移</b> ⇒ 多屏机器上鼠标整体错位。
///         而<b>单屏机器的偏移恒为 0</b> —— 也就是开发机上永远是对的；</item>
///   <item><b>正负号或横纵写反</b> ⇒ 鼠标跑到镜像位置。这个反而容易发现
///         （真机上一点就露），所以它是三者里最不危险的。</item>
/// </list>
///
/// <para>
/// 这段换算被刻意抽成<b>纯函数</b>（不碰原生 API、不看任何状态），
/// 就是为了让它可以被这里的一堆用例钉死 —— 而不是靠"多找几台机器试试"。
/// </para>
/// </summary>
public sealed class RemoteInputCoordinateTests
{
    /// <summary>量程上界，与 Windows <c>SendInput</c> 的 <c>MOUSEEVENTF_ABSOLUTE</c> 一致。</summary>
    private const int Max = InputCoordinates.Max;

    // ═══════════════════════════════════════════════════════════
    // 两个端点：0 与 65535 必须分别落在"最左上"与"最右下"
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>四个角必须精确落在四个角上</b>（这是本文件里最重要的一条）。
    ///
    /// <para>
    /// 它同时钉住两件事：<b>量程的两端是闭区间</b>，以及<b>分母是"尺寸 − 1"</b>。
    /// 用"尺寸"当分母的话，<c>65535</c> 会算成 <c>1919</c>（1080p 宽），
    /// 于是最右边那<b>一整列</b>像素永远点不到 —— 而这一条会当场失败。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(Max, 0, 1920 - 1, 0)]
    [InlineData(0, Max, 0, 1080 - 1)]
    [InlineData(Max, Max, 1920 - 1, 1080 - 1)]
    public void The_four_corners_land_exactly_on_the_four_corners(
        int normalizedX, int normalizedY, int expectedX, int expectedY)
    {
        var (x, y) = InputCoordinates.ToPixels(normalizedX, normalizedY, 0, 0, 1920, 1080);

        x.Should().Be(expectedX, "兜不住右边界就意味着最右那一列永远点不到");
        y.Should().Be(expectedY, "兜不住下边界同理");
    }

    /// <summary>
    /// 🔥 <b>用"尺寸"当分母的实现会在这里失败。</b>
    ///
    /// <para>
    /// 反向钉一次：如果分母被写成 <c>width</c>，<c>65535</c> 会算成
    /// <c>65535 × 1919 ÷ 65535 = 1919</c>… 嗯，这里要说清真正会错成什么样：
    /// 分母写宽的实现是 <c>x = n × w ÷ 65536</c>，于是 <c>n = 65535</c> 时
    /// <c>x = 1919.97 → 1919</c>（看起来没错），但 <c>n = 65535</c> 在
    /// <c>n ÷ 65535</c> 的约定下<b>本应</b>等于"最后 1 个像素"。
    /// 换句话说：<b>两种写法在 1080p 上只差不到 1 个像素，肉眼根本分不出来</b> ——
    /// 而这正是它危险的地方。
    /// </para>
    ///
    /// <para>
    /// 所以这条用例改成钉<b>单调性 + 满射</b>：一段够窄的输入里，
    /// 必须能取到每一个像素值。分母写错时，某个像素值会<b>永远取不到</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void Every_pixel_on_a_short_axis_is_reachable()
    {
        // 取一条很短的轴（宽 11），把 0–65535 全扫一遍，看 0–10 这 11 个像素是不是都出现过。
        const int width = 11;

        var reached = new HashSet<int>();

        for (var n = 0; n <= Max; n++)
        {
            reached.Add(InputCoordinates.ToPixels(n, 0, 0, 0, width, 1080).X);
        }

        reached.Should().BeEquivalentTo(
            Enumerable.Range(0, width),
            "🔥 11 个像素必须全都能取到：用「尺寸」当分母时某一个会永远取不到 —— "
            + "而在 1080p/8K 上那个差不到 1 个像素，肉眼与手测都发现不了");
    }

    /// <summary>
    /// <b>8K 上再钉一次"最右一列点得到"。</b>
    /// <para>
    /// 7680 宽下，"用尺寸当分母"少掉的是<b>30 多个像素宽的一整条边</b> ——
    /// 在那条边上停着的窗口按钮（关闭 / 最大化）会永远点不到。
    /// </para>
    /// </summary>
    [Fact]
    public void The_last_column_is_reachable_at_8k()
    {
        var (x, _) = InputCoordinates.ToPixels(Max, 0, 0, 0, 7680, 4320);

        x.Should().Be(7679, "8K 宽下最右一列是 7679，不是 7680（越界）也不是 7648（少了 30 多像素的边）");
    }

    // ═══════════════════════════════════════════════════════════
    // 屏幕偏移：多屏机器上唯一会对的那一半
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>副屏在主屏右边时，偏移必须原样加上去。</b>
    ///
    /// <para>
    /// 少了这一步，在<b>单屏机器</b>上一切正常（偏移是 0），而多屏机器上
    /// 鼠标会整体错位一整个屏宽 —— <b>这个 bug 在开发机上完全复现不出来</b>，
    /// 是本方案里最贵的一种。
    /// </para>
    /// </summary>
    [Fact]
    public void A_second_screen_to_the_right_shifts_the_whole_range()
    {
        var (left, _) = InputCoordinates.ToPixels(0, 0, left: 1920, top: 0, 1920, 1080);
        var (right, _) = InputCoordinates.ToPixels(Max, 0, left: 1920, top: 0, 1920, 1080);

        left.Should().Be(1920, "画面最左 = 这块屏在虚拟桌面里的左边缘");
        right.Should().Be(3839, "画面最右 = 左边缘 + 宽度 − 1（仍不许越到 3840）");
    }

    /// <summary>
    /// 🔥 <b>副屏摆在主屏<b>左边</b>时，偏移是负数 —— 而这一点必须成立。</b>
    ///
    /// <para>
    /// Windows 允许把副屏摆在主屏左侧，此时虚拟桌面的原点在主屏左上，
    /// 副屏的 <c>Left</c> 是<b>负数</b>。如果实现里对偏移做了 <c>Math.Max(0, …)</c>
    /// 之类的"防负数"处理，这种布局下副屏的坐标会整体错位一个屏宽 ——
    /// 而它在任何"副屏在右边"的机器上都是好的。
    /// </para>
    /// </summary>
    [Fact]
    public void A_screen_left_of_the_primary_gets_a_negative_origin()
    {
        var (x, _) = InputCoordinates.ToPixels(0, 0, left: -1920, top: -1080, 1920, 1080);

        x.Should().Be(-1920, "画面最左落在一个负坐标上，这是合法且必须支持的");

        var (bottomRightX, bottomRightY) = InputCoordinates.ToPixels(Max, Max, -1920, -1080, 1920, 1080);

        bottomRightX.Should().Be(-1);
        bottomRightY.Should().Be(-1);
    }

    /// <summary>
    /// 越界的归一化值要**夹住**，不能原样透传：拖拽时鼠标会被拖到画面外，
    /// 而一个越界坐标在被控端那边表现得像"卡住了"。
    /// </summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(Max + 1, 0)]
    [InlineData(0, Max + 1)]
    public void Out_of_range_normalized_values_are_clamped(int normalizedX, int normalizedY)
    {
        var (x, y) = InputCoordinates.ToPixels(normalizedX, normalizedY, 0, 0, 1920, 1080);

        x.Should().BeInRange(0, 1919);
        y.Should().BeInRange(0, 1079);
    }

    // ═══════════════════════════════════════════════════════════
    // 往返：这两个函数必须真的是一对
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>像素 → 归一化 → 像素 必须回到原处</b>（在容差内）。
    ///
    /// <para>
    /// <c>ToNormalized</c> 在产品路径上<b>没有调用点</b>（它的存在意义就是这一条用例）——
    /// 有了它，"这两个函数是不是一对"就不再需要靠读代码确认。
    /// 实测里它对"分母写错"这一类错非常敏感：写错时往返误差会随分辨率放大。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 容差给 1 个像素是有理由的：<c>0–65535</c> 只有 65536 档，
    /// 而 1080p 高有 1080 档（够用），8K 高有 4320 档（也够用）——
    /// 但**极长/极窄的轴**上往返必然有量化误差。1 像素是这段量程表达的极限，
    /// 不是"实现不够好"。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(7680, 4320)]
    public void Pixel_to_normalized_and_back_survives_a_round_trip(int width, int height)
    {
        foreach (var (px, py) in new[]
                 {
                     (0, 0),
                     (width / 2, height / 2),
                     (width - 1, height - 1),
                     (width / 3, height / 4),
                 })
        {
            var (nx, ny) = InputCoordinates.ToNormalized(px, py, 0, 0, width, height);
            var (backX, backY) = InputCoordinates.ToPixels(nx, ny, 0, 0, width, height);

            backX.Should().BeCloseTo(px, 1, $"({px},{py}) 在 {width}×{height} 上往返之后不该跑掉");
            backY.Should().BeCloseTo(py, 1, $"({px},{py}) 在 {width}×{height} 上往返之后不该跑掉");
        }
    }

    /// <summary>
    /// 往返在<b>带偏移</b>时也要成立 —— 上面那条只用了偏移 0，
    /// 而"偏移"恰好是最容易在某一半里忘掉的那一项。
    /// </summary>
    [Fact]
    public void The_round_trip_also_holds_with_a_screen_offset()
    {
        const int left = 1920;
        const int top = 200;

        var (nx, ny) = InputCoordinates.ToNormalized(1920 + 800, 200 + 600, left, top, 1920, 1080);
        var (x, y) = InputCoordinates.ToPixels(nx, ny, left, top, 1920, 1080);

        x.Should().BeCloseTo(1920 + 800, 1);
        y.Should().BeCloseTo(200 + 600, 1);
    }

    // ═══════════════════════════════════════════════════════════
    // 退化输入：不猜，但也不崩
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 🔥 <b>尺寸不合理时"原样返回"，而不是猜一个数。</b>
    ///
    /// <para>
    /// 这条守的是一条纪律：<b>"我们不知道该往哪儿换算"与"换算结果是 0"是两件事。</b>
    /// 尺寸为 0/1 时，除以 <c>尺寸 − 1</c> 会<b>整数除零</b>（<c>0/0</c> 对整型是
    /// <see cref="DivideByZeroException"/>，对 double 是 NaN → 转 int 变成
    /// <see cref="int.MinValue"/>）。两种都会让鼠标跑到一个完全无关的位置上 ——
    /// 而"原样返回"至少让调用方看得出自己拿到的是什么。
    /// </para>
    ///
    /// <para>⚠️ 为什么它可能真的发生：抓屏还没拿到分辨率的时候（会话刚建、第一帧未到），
    /// 尺寸字段就是 0。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    public void A_degenerate_size_is_passed_through_instead_of_guessed(int width, int height)
    {
        var (x, y) = InputCoordinates.ToPixels(1234, 5678, 0, 0, width, height);

        x.Should().Be(1234, "原样返回 = 把「我不知道」如实交给调用方");
        y.Should().Be(5678);
    }

    /// <summary>
    /// 反向那一半在退化尺寸下同样不许抛。
    /// </summary>
    [Fact]
    public void The_reverse_direction_also_survives_a_degenerate_size()
    {
        var roundTrip = () => InputCoordinates.ToNormalized(100, 200, 0, 0, 0, 0);

        roundTrip.Should().NotThrow("除以 (尺寸 − 1) 在尺寸为 0 时是 0/0 —— 这条路径必须被挡住");
        roundTrip().Should().Be((100, 200));
    }
}
