using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>What a genuine refresh-token value names: its session and its own row.</summary>
/// <param name="Session">The session the token belongs to.</param>
/// <param name="Id">The token row's id.</param>
internal readonly record struct RefreshTokenValue(Guid Session, Guid Id);

/// <summary>
/// The derivable refresh-token value (spec D6, bound to its session by #527): <c>alvo_rt_&lt;session&gt;_&lt;id&gt;_&lt;mac&gt;</c>,
/// where the session and the id are uuids in lower-case <c>N</c> format and the MAC is
/// base64url(HMAC-SHA256(key, <c>session_id</c>)). The server re-derives a child's value from its session and id, so the
/// store keeps no secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the session is in the value</b> (#527, hardening design §5.2 option B): reuse detection (RFC 9700 §4.14.2)
/// works by recognising a rotated token, and the sweep deletes rotated rows past the grace window. A value the MAC proves
/// genuine whose row is gone but whose session is live can only be a swept ancestor, so it still ends the session — the
/// reuse signal no longer needs the ancestors' rows. The session is inside the MAC, so a value cannot be moved to another.
/// </para>
/// <para>
/// <b>Parsed by position, never split on <c>_</c>.</b> Base64url's alphabet contains <c>_</c>, so a MAC may start with
/// one; the prefix is 8 characters, the session 32, a separator, the id 32, a separator, the MAC 43.
/// </para>
/// <para>
/// <b>Strictly canonical.</b> Both uuids must be lower-case hex and the MAC is compared as text, in constant time, against
/// the one encoding the server produces — so a value that decodes to the same bytes through another spelling is not the
/// token. The recognisable prefix is what secret scanners match on.
/// </para>
/// </remarks>
internal static class RefreshTokenFormat
{
    /// <summary>The prefix every refresh token starts with.</summary>
    internal const string Prefix = "alvo_rt_";

    private const int IdLength = 32;

    private const int MacLength = 43;

    private const char Separator = '_';

    private const int IdIndex = 8 + IdLength + 1;

    private const int SeparatorIndex = IdIndex + IdLength;

    private const int TokenLength = SeparatorIndex + 1 + MacLength;

    private static readonly SearchValues<char> _lowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>The value of the refresh token <paramref name="id"/> of <paramref name="session"/>, under <paramref name="key"/>.</summary>
    /// <param name="session">The session the token belongs to.</param>
    /// <param name="id">The token row's id.</param>
    /// <param name="key">The refresh HMAC key.</param>
    internal static string Format(Guid session, Guid id, ReadOnlySpan<byte> key)
    {
        RequireKey(key);
        var text = $"{session:N}{Separator}{id:N}";
        return $"{Prefix}{text}{Separator}{Mac(text, key)}";
    }

    /// <summary>Reads the session and id out of <paramref name="value"/> when its MAC under <paramref name="key"/> is the one it carries.</summary>
    /// <param name="value">The presented value.</param>
    /// <param name="key">The refresh HMAC key.</param>
    /// <param name="token">The session and id, when the value is genuine.</param>
    internal static bool TryParse(string? value, ReadOnlySpan<byte> key, out RefreshTokenValue token)
    {
        token = default;
        if (key.IsEmpty || !HasShape(value))
        {
            return false;
        }

        var text = value.AsSpan(Prefix.Length, SeparatorIndex - Prefix.Length).ToString();
        if (!CryptographicOperations.FixedTimeEquals(Bytes(Mac(text, key)), Bytes(value.AsSpan(SeparatorIndex + 1))))
        {
            return false;
        }

        token = new RefreshTokenValue(
            Guid.ParseExact(value.AsSpan(Prefix.Length, IdLength), "N"), Guid.ParseExact(value.AsSpan(IdIndex, IdLength), "N"));
        return true;
    }

    private static bool HasShape([NotNullWhen(true)] string? value)
        => value is { Length: TokenLength }
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value[IdIndex - 1] == Separator
            && value[SeparatorIndex] == Separator
            && IsLowerHex(value.AsSpan(Prefix.Length, IdLength))
            && IsLowerHex(value.AsSpan(IdIndex, IdLength));

    private static bool IsLowerHex(ReadOnlySpan<char> text) => !text.ContainsAnyExcept(_lowerHex);

    private static string Mac(string text, ReadOnlySpan<byte> key)
        => Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(text)));

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<char> text) => MemoryMarshal.AsBytes(text);

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("A refresh token needs a non-empty HMAC key.", nameof(key));
        }
    }
}
