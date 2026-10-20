using System.Security.Cryptography;
using ControlServer.Core.Abstractions;

namespace ControlServer.Core.Services;

/// <summary>SHA256 加盐哈希（M1 兼容实现，M3 迁移到 PBKDF2）。</summary>
public sealed class Sha256PasswordHasher : IPasswordHasher
{
    public (string Hash, string Salt) Hash(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return (Compute(password, salt), salt);
    }

    public bool Verify(string password, string hash, string salt)
    {
        var computed = Compute(password, salt);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computed),
            System.Text.Encoding.UTF8.GetBytes(hash));
    }

    private static string Compute(string password, string salt)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(salt + ":" + password)));
}
