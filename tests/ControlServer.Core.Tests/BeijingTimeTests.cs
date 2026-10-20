using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

public sealed class BeijingTimeTests
{
    [Fact]
    public void Now_is_always_utc_plus_8()
    {
        BeijingTime.Now.Offset.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public void ToBeijing_converts_any_offset_to_beijing()
    {
        var utc = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);

        var beijing = BeijingTime.ToBeijing(utc);

        beijing.Offset.Should().Be(TimeSpan.FromHours(8));
        beijing.Hour.Should().Be(11);
        beijing.Date.Should().Be(new DateTime(2026, 10, 7));
    }

    [Fact]
    public void Format_uses_airport_style_timestamp()
    {
        var value = new DateTimeOffset(2026, 10, 7, 11, 41, 12, TimeSpan.FromHours(8));

        BeijingTime.Format(value).Should().Be("2026-10-07 11:41:12");
    }
}
