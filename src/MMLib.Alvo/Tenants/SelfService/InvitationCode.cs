using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// An invitation's secret code (S4 plan D2, spec §6.1): <see cref="Prefix"/> followed by 128 bits from
/// <see cref="RandomNumberGenerator"/> in base64url without padding — 31 characters — shown to its owner once and stored
/// only as <see cref="Hashed"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The prefix is GitHub's secret-scanning precedent</b>, as spec §7.1's <c>alvo_pat_</c> is: a leaked code is
/// recognisable by shape.
/// </para>
/// <para>
/// <b>Unsalted SHA-256 is enough</b>: the secret carries 128 bits of entropy, so there is no dictionary to precompute.
/// <see cref="Hash"/> takes <em>any</em> presented text and a malformed code is simply not found — one path, one answer.
/// </para>
/// <para>
/// <b>Only <see cref="Value"/> yields the secret.</b> <see cref="ToString"/> — and so every interpolation, log template
/// and formatted message — prints <c>alvo_inv_***</c>.
/// </para>
/// </remarks>
internal readonly struct InvitationCode
{
    /// <summary>The prefix every invitation code starts with.</summary>
    internal const string Prefix = "alvo_inv_";

    private const int SecretBytes = 16;

    private InvitationCode(string value)
    {
        Value = value;
        Hashed = Hash(value);
    }

    /// <summary>Gets the code itself — the only accessor that yields the secret.</summary>
    internal string Value { get; }

    /// <summary>Gets the code's hash, the only form of it that is ever stored.</summary>
    internal string Hashed { get; }

    /// <summary>Mints a fresh code from the system's cryptographically secure generator.</summary>
    internal static InvitationCode New() =>
        new(Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes)));

    /// <summary>The lowercase hex SHA-256 of <paramref name="presented"/>'s UTF-8 — of any text, a code or not.</summary>
    /// <param name="presented">The text a caller presented as a code.</param>
    internal static string Hash(string presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }

    /// <summary>Returns the prefix and a mask — never the code.</summary>
    public override string ToString() => Prefix + "***";
}
