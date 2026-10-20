using ControlServer.Core.Services;
using FluentAssertions;

namespace ControlServer.Core.Tests;

public sealed class Sha256PasswordHasherTests
{
    private readonly Sha256PasswordHasher _hasher = new();

    [Fact]
    public void Verify_accepts_correct_password()
    {
        var (hash, salt) = _hasher.Hash("admin123");

        _hasher.Verify("admin123", hash, salt).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var (hash, salt) = _hasher.Hash("admin123");

        _hasher.Verify("admin124", hash, salt).Should().BeFalse();
    }

    [Fact]
    public void Salt_is_random_per_call()
    {
        var (hash1, salt1) = _hasher.Hash("same");
        var (hash2, salt2) = _hasher.Hash("same");

        salt1.Should().NotBe(salt2);
        hash1.Should().NotBe(hash2);
    }
}
