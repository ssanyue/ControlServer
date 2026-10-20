using ControlServer.Agent.Transport;
using FluentAssertions;

namespace ControlServer.Agent.Tests;

/// <summary>
/// 「这一轮该连哪个地址」的判定（<see cref="ServerAddressResolver"/>）。
///
/// <para>
/// 🔥 <b>这个文件对应一条真实事故</b>（2026-10-20 09:15–09:20 的客户端日志）：
/// 客户端比中心<b>先启动</b>，而 <c>credentials.dat</c> 里记着上一次连过的地址
/// （当时已经不在那个网段了）。旧代码把这份缓存当**终结答案** —— 只要它有值，
/// 广播发现一步都不走，于是十几轮"连接 192.168.3.33:8899 失败"，
/// 而中心明明就在本机、广播一喊就应答。人只能到本机页上手动改地址（改完一秒就连上）。
/// </para>
///
/// <para>
/// 所以这里钉的是<b>三条互相制约的规则</b>，任何一条写歪都会掉进一个具体的坑：
/// </para>
/// <list type="number">
///   <item>人指定的地址<b>不许</b>被自动替换（同网段两套中心时会被悄悄挪走）；</item>
///   <item>缓存地址要先信一次（每轮都广播＝每次重连白等 9 秒）；</item>
///   <item>缓存地址连续失败两次之后<b>必须</b>去问广播（否则永远回不来）。</item>
/// </list>
/// </summary>
public class ServerAddressResolverTests
{
    private const string Remembered = "http://10.0.0.5:8899";
    private const string Other = "http://10.0.0.9:8899";

    // ───────────────────────────────────────────────────────────────────────
    // 什么时候该广播
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_remembered_address_must_discover()
    {
        ServerAddressResolver.WantsDiscovery(null, 0, discoveryAllowed: true)
            .Should().BeTrue("没有地址时广播是唯一的出路（装完就自己出现）");
    }

    [Fact]
    public void Fresh_remembered_address_is_trusted_once()
    {
        // 🔥 这个用例保护的是"装机 99% 的时间"：上次连上过，这次先直连它。
        //    写成"永远先广播"会让每次开机白等 9 秒，而且完全没必要。
        ServerAddressResolver.WantsDiscovery(Remembered, 0, discoveryAllowed: true)
            .Should().BeFalse();

        ServerAddressResolver.WantsDiscovery(Remembered, 1, discoveryAllowed: true)
            .Should().BeFalse("失败一次可能只是中心正在重启，别急着换地址");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(20)]
    public void Remembered_address_that_keeps_failing_must_discover(int failures)
    {
        ServerAddressResolver.WantsDiscovery(Remembered, failures, discoveryAllowed: true)
            .Should().BeTrue("连续失败之后不去问广播，就永远回不来 —— 这正是那条事故");
    }

    [Fact]
    public void Discovery_switch_is_off_when_broadcast_is_disabled()
    {
        // 跨网段部署时广播本来就不通，这里多花 9 秒只会拖慢重试。
        ServerAddressResolver.WantsDiscovery(null, 5, discoveryAllowed: false)
            .Should().BeFalse();

        ServerAddressResolver.WantsDiscovery(Remembered, 5, discoveryAllowed: false)
            .Should().BeFalse();
    }

    // ───────────────────────────────────────────────────────────────────────
    // 挑哪个地址
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Keeps_remembered_address_before_the_threshold()
    {
        var choice = ServerAddressResolver.Resolve(Remembered, Other, consecutiveFailures: 1, discoveryAllowed: true);

        choice.Url.Should().Be(Remembered);
        choice.Source.Should().Be(ServerAddressSource.Remembered);
        choice.Note.Should().BeNull("一切正常时不许产生日志噪音");
    }

    [Fact]
    public void Switches_to_the_discovered_center_after_repeated_failures()
    {
        var choice = ServerAddressResolver.Resolve(Remembered, Other, consecutiveFailures: 2, discoveryAllowed: true);

        choice.Url.Should().Be(Other);
        choice.Source.Should().Be(ServerAddressSource.Discovered);
        choice.Note.Should().Contain(Remembered).And.Contain(Other,
            "换址必须一眼看出'从哪换到哪' —— 这是现场唯一能解释'为什么机器跑到别的中心去了'的线索");
    }

    [Fact]
    public void Keeps_remembered_address_when_discovery_finds_nothing()
    {
        var choice = ServerAddressResolver.Resolve(Remembered, null, consecutiveFailures: 3, discoveryAllowed: true);

        choice.Url.Should().Be(Remembered,
            "广播没找到中心不代表这个地址作废了（中心可能只是还没起来）");
        choice.Source.Should().Be(ServerAddressSource.Remembered);
        choice.Note.Should().Contain("广播也没有找到");
    }

    [Fact]
    public void Keeps_remembered_address_when_discovery_finds_the_same_one()
    {
        // 🔥 这一条防的是"每轮都重新注册"：发现到的地址和记住的一样时判成'换了地址'，
        //    会让客户端每两轮就清一次凭据、重新注册一次，服务端列表里会不断冒出同一台机器。
        var choice = ServerAddressResolver.Resolve(Remembered, "HTTP://10.0.0.5:8899/", consecutiveFailures: 2, discoveryAllowed: true);

        choice.Url.Should().Be(Remembered);
        choice.Source.Should().Be(ServerAddressSource.Remembered);
        choice.Note.Should().BeNull("同一个地址不算换址，也没有可说的问题");
    }

    [Fact]
    public void First_boot_without_credentials_uses_discovery()
    {
        var choice = ServerAddressResolver.Resolve(null, Other, consecutiveFailures: 0, discoveryAllowed: true);

        choice.Url.Should().Be(Other);
        choice.Source.Should().Be(ServerAddressSource.Discovered);
    }

    [Fact]
    public void Nothing_usable_reports_none()
    {
        var choice = ServerAddressResolver.Resolve(null, null, consecutiveFailures: 0, discoveryAllowed: true);

        choice.Url.Should().BeNull();
        choice.Source.Should().Be(ServerAddressSource.None);
        choice.Note.Should().Contain("未发现", "状态栏那行文案就靠它");
    }

    [Fact]
    public void Discovered_address_is_ignored_when_broadcast_is_disabled()
    {
        // 广播被关掉时，即便调用方误传了一个发现结果，也不许拿它去连
        // （那种地址通常来自别的网段，连过去只会更慢）。
        var choice = ServerAddressResolver.Resolve(Remembered, Other, consecutiveFailures: 9, discoveryAllowed: false);

        choice.Url.Should().Be(Remembered);
        choice.Source.Should().Be(ServerAddressSource.Remembered);
    }

    [Fact]
    public void Negative_failure_count_is_treated_as_zero()
    {
        // 调用方忘了初始化时不该突然跳过缓存地址（否则第一轮就去广播，白等 9 秒）。
        var choice = ServerAddressResolver.Resolve(Remembered, Other, consecutiveFailures: -5, discoveryAllowed: true);

        choice.Url.Should().Be(Remembered);
        ServerAddressResolver.WantsDiscovery(Remembered, -5, discoveryAllowed: true).Should().BeFalse();
    }

    // ───────────────────────────────────────────────────────────────────────
    // 地址比较
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://10.0.0.5:8899", "http://10.0.0.5:8899/", true)]
    [InlineData("HTTP://10.0.0.5:8899", "http://10.0.0.5:8899", true)]
    [InlineData("http://10.0.0.5:8899", "http://10.0.0.5:8900", false)]
    [InlineData("http://10.0.0.5:8899", "http://10.0.0.6:8899", false)]
    [InlineData("https://10.0.0.5:8899", "http://10.0.0.5:8899", false)]
    [InlineData(null, "http://10.0.0.5:8899", false)]
    public void Same_address_compares_authority(string? left, string? right, bool expected)
    {
        ServerAddressResolver.SameAddress(left, right).Should().Be(expected);
    }
}
