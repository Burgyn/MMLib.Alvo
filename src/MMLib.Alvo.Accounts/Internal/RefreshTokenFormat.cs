using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The derivable refresh-token value (spec D6): <c>alvo_rt_&lt;id&gt;_&lt;mac&gt;</c>, where the id is the token row's
/// uuid in lower-case <c>N</c> format and the MAC is base64url(HMAC-SHA256(key, id)). The server re-derives a child's
/// value from its id, so the store keeps no secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsed by position, never split on <c>_</c>.</b> Base64url's alphabet contains <c>_</c>, so a MAC may start with
/// one; the prefix is 8 characters, the id 32, the separator 1, the MAC 43.
/// </para>
/// <para>
/// <b>Strictly canonical.</b> The id must be lower-case hex and the MAC is compared as text, in constant time, against the
/// one encoding the server produces — so a value that decodes to the same bytes through another spelling is not the token.
/// The recognisable prefix is what secret scanners match on.
/// </para>
/// </remarks>
internal static class RefreshTokenFormat
{
    /// <summary>The prefix every refresh token starts with.</summary>
    internal const string Prefix = "alvo_rt_";

    private const int IdLength = 32;

    private const int MacLength = 43;

    private const char Separator = '_';

    private const int SeparatorIndex = 8 + IdLength;

    private const int TokenLength = SeparatorIndex + 1 + MacLength;

    private static readonly SearchValues<char> _lowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>The value of the refresh token <paramref name="id"/>, under <paramref name="key"/>.</summary>
    /// <param name="id">The token row's id.</param>
    /// <param name="key">The refresh HMAC key.</param>
    internal static string Format(Guid id, ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        var text = id.ToString("N");
        return $"{Prefix}{text}{Separator}{Mac(text, key)}";
    }

    /// <summary>Reads the id out of <paramref name="value"/> when its MAC under <paramref name="key"/> is the one it carries.</summary>
    /// <param name="value">The presented value.</param>
    /// <param name="key">The refresh HMAC key.</param>
    /// <param name="id">The token row's id, when the value is genuine.</param>
    internal static bool TryParse(string? value, ReadOnlySpan<byte> key, out Guid id)
    {
        id = Guid.Empty;
        if (key.IsEmpty || !HasShape(value))
        {
            return false;
        }

        var text = value.AsSpan(Prefix.Length, IdLength);
        var expected = Mac(text.ToString(), key);
        if (!CryptographicOperations.FixedTimeEquals(Bytes(expected), Bytes(value.AsSpan(SeparatorIndex + 1))))
        {
            return false;
        }

        id = Guid.ParseExact(text, "N");
        return true;
    }

    private static bool HasShape([NotNullWhen(true)] string? value)
        => value is { Length: TokenLength }
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value[SeparatorIndex] == Separator
            && IsLowerHex(value.AsSpan(Prefix.Length, IdLength));

    private static bool IsLowerHex(ReadOnlySpan<char> text) => !text.ContainsAnyExcept(_lowerHex);

    private static string Mac(string idText, ReadOnlySpan<byte> key)
        => Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(idText)));

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<char> text) => MemoryMarshal.AsBytes(text);

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("A refresh token needs a non-empty HMAC key.", nameof(key));
        }
    }
}
