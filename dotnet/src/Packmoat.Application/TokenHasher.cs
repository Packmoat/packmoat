using System.Security.Cryptography;
using System.Text;

namespace Packmoat.Application;

/// <summary>Tokens are stored only as SHA-256 hashes. Tokens are 192-bit random, so a plain hash is sufficient.</summary>
public static class TokenHasher
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool FixedTimeEquals(string hashA, string hashB) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hashA), Encoding.ASCII.GetBytes(hashB));
}
