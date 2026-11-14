using System.Buffers.Text;
using System.Security.Cryptography;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// A freshly minted app token's credential (S5 plan D3, spec §7.1): a key id of <see cref="PersonalPrefix"/> or
/// <see cref="ServicePrefix"/> plus base64url of 16 random bytes (31 characters, not secret), and a secret of base64url of
/// 32 random bytes (43 characters, 256 bits), presented as <c>keyId.secret</c> on the existing API-key header.
/// </summary>
/// <remarks>
/// <para>
/// <b>The prefix is GitHub's secret-scanning precedent</b>, and the whole credential starts with it, so a leaked token is
/// recognisable by shape. The prefixes are <b>reserved</b>: <see cref="AlvoAuthOptionsValidator"/> refuses a configured
/// key whose id starts with either, so <em>prefix ⇔ persisted app token</em> holds everywhere and
/// <see cref="KindOf"/> is sound.
/// </para>
/// <para>
/// <b>Stored as <see cref="ApiKeyHash.Compute"/> of the secret part</b> — the base64 SHA-256 every
/// <see cref="ApiKeyRecord.Sha256Hash"/> holds. Unsalted is right for a 256-bit random secret: there is no dictionary to
/// precompute (S4 plan D2's reasoning).
/// </para>
/// <para>
/// <b>Only <see cref="Presented"/> yields the secret.</b> <see cref="ToString"/> — and so every interpolation, log
/// template and formatted message — prints <c>alvo_pat_***</c> or <c>alvo_svc_***</c>.
/// </para>
/// </remarks>
internal readonly struct AppTokenSecret
{
    /// <summary>The prefix every personal token's key id starts with.</summary>
    internal const string PersonalPrefix = "alvo_pat_";

    /// <summary>The prefix every service token's key id starts with.</summary>
    internal const string ServicePrefix = "alvo_svc_";

    private const int KeyIdBytes = 16;
    private const int SecretBytes = 32;

    private readonly string _secret;

    private AppTokenSecret(string keyId, string secret)
    {
        KeyId = keyId;
        _secret = secret;
        Hash = ApiKeyHash.Compute(secret);
    }

    /// <summary>Gets the key id: the kind's prefix and 22 base64url characters; not secret.</summary>
    internal string KeyId { get; }

    /// <summary>Gets the whole credential, <c>keyId.secret</c> — the only accessor that yields the secret.</summary>
    internal string Presented => KeyId + "." + _secret;

    /// <summary>Gets the hash of the secret part, the only form of it that is ever stored.</summary>
    internal string Hash { get; }

    /// <summary>Mints a fresh credential of <paramref name="kind"/> from the system's cryptographically secure generator.</summary>
    /// <param name="kind"><see cref="ApiKeyKind.Personal"/> or <see cref="ApiKeyKind.Service"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not an app-token kind.</exception>
    internal static AppTokenSecret New(ApiKeyKind kind) => new(
        PrefixOf(kind) + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyIdBytes)),
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes)));

    /// <summary>Whether <paramref name="keyId"/> carries an app-token prefix (ordinal).</summary>
    /// <param name="keyId">A presented or stored key id.</param>
    internal static bool IsAppTokenId(string keyId) => KindOf(keyId) is not ApiKeyKind.Configured;

    /// <summary>The kind <paramref name="keyId"/>'s prefix names; <see cref="ApiKeyKind.Configured"/> when it has none.</summary>
    /// <param name="keyId">A presented or stored key id.</param>
    internal static ApiKeyKind KindOf(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (keyId.StartsWith(PersonalPrefix, StringComparison.Ordinal))
        {
            return ApiKeyKind.Personal;
        }

        return keyId.StartsWith(ServicePrefix, StringComparison.Ordinal) ? ApiKeyKind.Service : ApiKeyKind.Configured;
    }

    /// <summary>Returns the prefix and a mask — never the key id's random part, never the secret.</summary>
    public override string ToString() => (KeyId is null ? string.Empty : PrefixOf(KindOf(KeyId))) + "***";

    private static string PrefixOf(ApiKeyKind kind) => kind switch
    {
        ApiKeyKind.Personal => PersonalPrefix,
        ApiKeyKind.Service => ServicePrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only personal and service tokens are minted."),
    };
}
