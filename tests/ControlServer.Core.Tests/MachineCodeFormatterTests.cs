using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

public sealed class MachineCodeFormatterTests
{
    [Fact]
    public void Produces_expected_shape()
    {
        var code = MachineCodeFormatter.ToMachineCode("CPU|BOARD|UUID");

        code.Should().MatchRegex("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$");
    }

    [Fact]
    public void Is_deterministic_for_same_fingerprint()
    {
        var a = MachineCodeFormatter.ToMachineCode("same-fingerprint");
        var b = MachineCodeFormatter.ToMachineCode("same-fingerprint");

        a.Should().Be(b);
    }

    [Fact]
    public void Differs_for_different_fingerprints()
    {
        var a = MachineCodeFormatter.ToMachineCode("fingerprint-a");
        var b = MachineCodeFormatter.ToMachineCode("fingerprint-b");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Falls_back_to_machine_name_when_fingerprint_blank()
    {
        var code = MachineCodeFormatter.ToMachineCode("   ");

        code.Should().NotBeNullOrWhiteSpace();
        code.Length.Should().Be(19);
    }
}
