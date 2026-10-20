using ControlServer.Contracts;
using FluentAssertions;

namespace ControlServer.Core.Tests;

/// <summary>
/// <b>远控质量档位 → 帧率/码率上界</b>（M7.4 新增「流畅（60 帧）」）。
///
/// <para>
/// 🔥 <b>为什么这几个数字值得单独钉</b>：档位是<b>三处共用</b>的一份表
/// （中心判降档、被控端设编码器、页面显示"现在是什么档"）。
/// 三处各写一份，迟早出现"页面写着流畅 60、编码器按 12 跑"——
/// 而那种不一致<b>没有任何一层会报错</b>。
/// </para>
///
/// <para>
/// ⚠️ 这些数是<b>配额上界</b>，不是实测值：实测由页面上的码率/帧率显示。
/// 所以用例钉的是"上界表本身"，不是"现场一定能跑到"。
/// </para>
/// </summary>
public sealed class RemoteQualityProfileTests
{
    /// <summary>60 帧档必须真的解析成 60，而且码率要够它用（否则画质会烂到看不出 60 的意义）。</summary>
    [Fact]
    public void The_smooth_profile_asks_for_sixty_fps()
    {
        var (fps, kbps) = RemoteQualityProfiles.Resolve(RemoteQualityProfiles.Smooth);

        fps.Should().Be(60, "「流畅」这一档存在的唯一理由就是 60 帧");
        kbps.Should().BeGreaterThan(
            RemoteQualityProfiles.Resolve(RemoteQualityProfiles.Balanced).BitrateKbps,
            "帧率翻五倍而码率不动，画面会糊成一片 —— 那不是流畅");
    }

    /// <summary>新档位必须被认成"已知档"，否则会被 <c>Normalize</c> 悄悄打回 auto。</summary>
    [Fact]
    public void The_smooth_profile_survives_normalization()
    {
        RemoteQualityProfiles.IsKnown(RemoteQualityProfiles.Smooth).Should().BeTrue();
        RemoteQualityProfiles.Normalize(RemoteQualityProfiles.Smooth)
            .Should().Be(RemoteQualityProfiles.Smooth, "认不得的档位会被打回 auto，页面上就变成「自动」");
    }

    /// <summary>
    /// 四个既有档位的数字<b>一个都不许动</b> —— 提 60 帧不该顺手改别人。
    /// </summary>
    [Theory]
    [InlineData(RemoteQualityProfiles.Low, 12, 3_000)]
    [InlineData(RemoteQualityProfiles.Balanced, 12, 6_000)]
    [InlineData(RemoteQualityProfiles.High, 30, 20_000)]
    public void The_existing_profiles_are_unchanged(string profile, int fps, int kbps)
    {
        RemoteQualityProfiles.Resolve(profile).Should().Be((fps, kbps));
    }

    /// <summary>
    /// <c>auto</c> 仍然等于 <c>balanced</c>：它<b>不是第四套数字</b>，
    /// 只是"从均衡起步、由 governor 往下调"。
    /// </summary>
    [Fact]
    public void Auto_is_still_balanced_and_unknown_names_still_fall_back()
    {
        RemoteQualityProfiles.Resolve(RemoteQualityProfiles.Auto)
            .Should().Be(RemoteQualityProfiles.Resolve(RemoteQualityProfiles.Balanced));

        RemoteQualityProfiles.Resolve("nonsense")
            .Should().Be(RemoteQualityProfiles.Resolve(RemoteQualityProfiles.Balanced));

        RemoteQualityProfiles.Normalize("nonsense").Should().Be(RemoteQualityProfiles.Auto);
    }

    /// <summary>
    /// <b>帧率以请求为准，档位只提供"没指定时的默认值"</b>（M7.16）。
    ///
    /// <para>
    /// 🔥 这条用例钉的是<b>一个曾经整块失效的控件</b>：旧折法是
    /// <c>Min(请求, 档位)</c>，而默认档位 <c>auto</c> 解析成 12 fps ⇒
    /// 页面上的「帧率上限」选 20 / 30 / 60 <b>都还是 12</b>，且没有任何一层报错。
    /// 现场的表现只有一句"选了没用"。
    /// </para>
    /// <para>
    /// ⚠️ 反过来也要守住：<b>没有请求值时必须回到档位的推荐值</b>。把 <c>null</c> 当成 0
    /// 会让"没指定"变成 1 fps —— 一个比"选了没用"更坏的静默故障。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, 12, 12)]
    [InlineData(null, 60, 60)]
    [InlineData(30, 12, 30)]
    [InlineData(60, 12, 60)]
    [InlineData(60, 60, 60)]
    [InlineData(5, 60, 5)]
    [InlineData(120, 60, 60)]
    [InlineData(0, 12, 1)]
    public void The_requested_fps_wins_over_the_profile(int? requested, int profileFps, int expected)
    {
        RemoteQualityProfiles.FoldTargetFps(requested, profileFps).Should().Be(expected);
    }

    /// <summary>
    /// <b>60 fps 配「低」档也是 60 fps</b> —— 帧率与码率从此是两件事。
    ///
    /// <para>
    /// 🔥 这条<b>故意钉住一个"看起来不合理"的组合</b>：3 Mbps / 60 fps ⇒ 每帧 50 kbps，
    /// 画面必然糊。它是对的，因为这是<b>用户自己选的</b>（他要 60 帧、要省带宽），
    /// 页面靠"档位"与"生效帧率"两个读数让他看见代价。
    /// 如果哪天有人想"顺手帮他降回 12"，这条用例会立刻红 —— 而那正是要拦的：
    /// <b>悄悄改用户的明确选择，比画面糊难查得多</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void Sixty_fps_survives_the_low_bitrate_profile()
    {
        var (profileFps, kbps) = RemoteQualityProfiles.Resolve(RemoteQualityProfiles.Low);

        RemoteQualityProfiles.FoldTargetFps(60, profileFps)
            .Should().Be(60, "用户明确要 60 帧 ⇒ 只降码率，不改帧率");

        kbps.Should().Be(3_000, "「低」仍然是 3 Mbps —— 帧率不管码率");
    }
}
