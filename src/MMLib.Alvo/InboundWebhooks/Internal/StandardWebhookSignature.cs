using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>
/// The Standard Webhooks symmetric signature: <c>v1,</c> + base64(HMAC-SHA256(key, <c>{id}.{timestamp}.{body}</c>)), the key
/// being the base64-decoded secret after its <c>whsec_</c> prefix. A header may carry several space-separated signatures
/// (the sender's zero-downtime rotation); entries of another version (<c>v1a</c>, asymmetric) are ignored.
/// </summary>
internal static class StandardWebhookSignature
{
    internal const string IdHeader = "webhook-id";
    internal const string TimestampHeader = "webhook-timestamp";
    internal const string SignatureHeader = "webhook-signature";

    private const string SecretPrefix = "whsec_";
    private const string Version = "v1";

    /// <summary>The shortest key accepted, in bytes: Standard Webhooks' secrets are 24 to 64 bytes (Ruling 54).</summary>
    internal const int MinKeyBytes = 24;

    /// <summary>The keys a stored secret value holds: one, or several separated by spaces (the receiver's rotation, Ruling 22).</summary>
    /// <exception cref="FormatException">
    /// A secret is not base64 or decodes to fewer than <see cref="MinKeyBytes"/> bytes, or the value holds no secret at all;
    /// the message says which, and never quotes a secret.
    /// </exception>
    internal static IReadOnlyList<byte[]> Keys(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        IReadOnlyList<byte[]> keys = [.. stored.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Key)];

        return keys.Count > 0 ? keys : throw new FormatException("It holds no secret.");
    }

    /// <summary>The <c>v1,…</c> signature of one delivery.</summary>
    internal static string Sign(byte[] key, string id, long timestamp, ReadOnlySpan<byte> body) =>
        $"{Version},{Convert.ToBase64String(Mac(key, id, timestamp, body))}";

    /// <summary>Whether any <c>v1</c> signature in <paramref name="header"/> is one of <paramref name="keys"/>' — compared in fixed time.</summary>
    internal static bool Verifies(IReadOnlyList<byte[]> keys, string id, long timestamp, ReadOnlySpan<byte> body, string header)
    {
        var offered = Offered(header);
        foreach (var key in keys)
        {
            var expected = Mac(key, id, timestamp, body);
            if (offered.Any(signature => CryptographicOperations.FixedTimeEquals(signature, expected)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<byte[]> Offered(string header) =>
        [.. header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Entry).OfType<byte[]>()];

    private static byte[]? Entry(string entry)
    {
        var comma = entry.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !string.Equals(entry[..comma], Version, StringComparison.Ordinal))
        {
            return null;
        }

        var buffer = new byte[entry.Length];
        return Convert.TryFromBase64String(entry[(comma + 1)..], buffer, out var written) && written > 0 ? buffer[..written] : null;
    }

    /// <summary>
    /// One secret's key. A short one is refused (Ruling 54): an empty key is one anyone can sign with, and Standard Webhooks
    /// asks for at least 24 bytes. A longer one is not — HMAC hashes a long key, so it is no weaker.
    /// </summary>
    private static byte[] Key(string secret)
    {
        var encoded = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        var key = new byte[encoded.Length];
        if (!Convert.TryFromBase64String(encoded, key, out var written))
        {
            throw new FormatException("A secret in it is not base64.");
        }

        return written >= MinKeyBytes
            ? key[..written]
            : throw new FormatException($"A secret in it decodes to a key shorter than {MinKeyBytes} bytes; Standard Webhooks' secrets are 24 to 64 bytes.");
    }

    private static byte[] Mac(byte[] key, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}."));
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));

        return HMACSHA256.HashData(key, content);
    }
}
