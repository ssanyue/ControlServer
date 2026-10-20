using System.Net;
using ControlServer.Server.Security;
using FluentAssertions;

namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// 来源网段白名单的纯判定逻辑（M6.2）。
///
/// <para>
/// 这一层<b>必须</b>单独测，因为它是"运维以为自己锁了边界"的唯一实现：
/// 网段判定错一位（比如 <c>/24</c> 当成 <c>/16</c>），现象是"该拦的没拦"，
/// 而这种错<b>不会</b>以任何报错的形式暴露出来。
/// </para>
/// </summary>
public sealed class NetworkAllowListTests
{
    [Fact]
    public void Empty_list_means_no_restriction()
    {
        var list = NetworkAllowList.Parse(null);

        list.IsEmpty.Should().BeTrue();
        list.Allows(IPAddress.Parse("203.0.113.9")).Should().BeTrue();
        list.Allows(null).Should().BeTrue("不限制时，连判不出来的来源也不该拦");
    }

    [Fact]
    public void Empty_strings_are_ignored_not_rejected()
    {
        // 配置里很常见 "192.168.1.0/24," 这种拖尾；为它拒绝启动不值得。
        var list = NetworkAllowList.Parse(new[] { "", "  ", "192.168.1.0/24" });

        list.Count.Should().Be(1);
    }

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("192.168.1.255", true)]
    [InlineData("192.168.2.5", false)]
    [InlineData("192.169.1.5", false)]
    [InlineData("10.0.0.1", false)]
    public void Cidr_matches_only_inside_the_range(string address, bool expected)
    {
        var list = NetworkAllowList.Parse(new[] { "192.168.1.0/24" });

        list.Allows(IPAddress.Parse(address)).Should().Be(expected);
    }

    [Fact]
    public void Slash_zero_allows_everything()
    {
        var list = NetworkAllowList.Parse(new[] { "0.0.0.0/0" });

        list.Allows(IPAddress.Parse("203.0.113.9")).Should().BeTrue();
    }

    [Fact]
    public void Loopback_is_always_allowed()
    {
        // 托盘、curl 127.0.0.1、探活脚本都走回环。挡住它们，
        // 现场就会出现"服务起来了，但本机打不开"这种最难自证的状态。
        var list = NetworkAllowList.Parse(new[] { "192.168.1.0/24" });

        list.Allows(IPAddress.Loopback).Should().BeTrue();
        list.Allows(IPAddress.IPv6Loopback).Should().BeTrue();
    }

    [Fact]
    public void Unknown_source_is_denied_when_a_list_is_configured()
    {
        var list = NetworkAllowList.Parse(new[] { "192.168.1.0/24" });

        list.Allows(null).Should().BeFalse("判不出来就不放 —— fail-closed");
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_normalized()
    {
        // 🔥 Kestrel 双栈监听时 RemoteIpAddress 报的是 ::ffff:192.168.1.5（16 字节），
        //    而配置里写的是 192.168.1.0/24（4 字节）。不归一化 ⇒ 逐字节比较必然不等 ⇒
        //    "白名单配对了，但所有人都被拒"。
        var list = NetworkAllowList.Parse(new[] { "192.168.1.0/24" });

        list.Allows(IPAddress.Parse("::ffff:192.168.1.5")).Should().BeTrue();
        list.Allows(IPAddress.Parse("::ffff:10.0.0.1")).Should().BeFalse();
    }

    [Fact]
    public void Bare_address_is_treated_as_a_single_host()
    {
        var list = NetworkAllowList.Parse(new[] { "192.168.1.7" });

        list.Allows(IPAddress.Parse("192.168.1.7")).Should().BeTrue();
        list.Allows(IPAddress.Parse("192.168.1.8")).Should().BeFalse();
    }

    [Fact]
    public void Ipv6_prefix_works()
    {
        var list = NetworkAllowList.Parse(new[] { "2001:db8::/32" });

        list.Allows(IPAddress.Parse("2001:db8:1234::1")).Should().BeTrue();
        list.Allows(IPAddress.Parse("2001:db9::1")).Should().BeFalse();
    }

    [Fact]
    public void Non_byte_aligned_prefix_is_honored()
    {
        // /26 = 64 个地址，范围 192.168.1.0 – 192.168.1.63。
        var list = NetworkAllowList.Parse(new[] { "192.168.1.0/26" });

        list.Allows(IPAddress.Parse("192.168.1.63")).Should().BeTrue();
        list.Allows(IPAddress.Parse("192.168.1.64")).Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.1.0/33")]      // IPv4 前缀越界
    [InlineData("192.168.1.0/-1")]
    [InlineData("192.168.1.0/abc")]
    [InlineData("300.1.1.1/24")]
    [InlineData("not-an-address")]
    public void Invalid_entry_refuses_to_parse(string raw)
    {
        // 🔥 fail-closed：写错一个字符就静默放行，等于把这层保护变成一句许愿。
        //    宁可启动失败，也不要"看起来起了"。
        var act = () => NetworkAllowList.Parse(new[] { raw });

        act.Should().Throw<FormatException>().WithMessage($"*{raw}*");
    }
}
