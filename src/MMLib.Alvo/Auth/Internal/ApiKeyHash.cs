using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// Hashes and verifies a dev API key's secret. The secret itself is never stored — only its
/// base64-encoded SHA-256 hash — and verification compares in constant time, so a wrong secret
/// and a byte-for-byte match take indistinguishable time.
/// </summary>
internal static class ApiKeyHash
{
    /// <summary>Computes the base64-encoded SHA-256 hash of <paramref name="secret"/>.</summary>
    /// <param name="secret">The plaintext secret to hash.</param>
    public static string Compute(string secret) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Answers whether <paramref name="secret"/> hashes to <paramref name="expectedHash"/>.</summary>
    /// <param name="secret">The plaintext secret presented by the caller.</param>
    /// <param name="expectedHash">The stored base64-encoded SHA-256 hash to compare against.</param>
    public static bool Matches(string secret, string expectedHash)
    {
        var computedBytes = Encoding.UTF8.GetBytes(Compute(secret));
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHash);
        return computedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(computedBytes, expectedBytes);
    }
}
