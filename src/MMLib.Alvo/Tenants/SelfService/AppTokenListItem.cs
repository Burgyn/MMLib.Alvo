using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Text.Json.Serialization;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// One app token as <c>GET {prefix}/tokens</c> lists it (S5 plan D14): everything about it except the credential, which the
/// registry never held — it keeps only a hash, and this record carries neither.
/// </summary>
/// <remarks>
/// Member names are fixed with <see cref="JsonPropertyNameAttribute"/> (the <see cref="AppTokenResponse"/> precedent).
/// <see cref="Status"/> is derived at the instant of the request, never stored: <c>revoked</c> beats <c>expired</c> beats
/// <c>active</c>.
/// </remarks>
/// <param name="KeyId">The token's key id — not secret, the one revoking takes.</param>
/// <param name="Kind"><c>personal</c> or <c>service</c>.</param>
/// <param name="Name">The token's name.</param>
/// <param name="TenantId">The tenant it acts in, or <see langword="null"/> for a tenantless personal token.</param>
/// <param name="Roles">A service token's tenant roles; empty for a personal one.</param>
/// <param name="Scopes">What it may read and write.</param>
/// <param name="CreatedBy">The person who minted it, under the one key every answer names a creator with (#542 item 1).</param>
/// <param name="CreatedAt">When it was minted.</param>
/// <param name="ExpiresAt">When it expires.</param>
/// <param name="LastUsedAt">When it last authenticated a request, to the minute, or <see langword="null"/> if never.</param>
/// <param name="RevokedAt">When it was revoked, or <see langword="null"/>.</param>
/// <param name="Status"><c>active</c>, <c>expired</c> or <c>revoked</c>, as of the request.</param>
internal sealed record AppTokenListItem(
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("createdBy")] PersonReference CreatedBy,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("status")] string Status)
{
    /// <summary>The <see cref="Status"/> of a token that works.</summary>
    internal const string ActiveStatus = "active";

    /// <summary>The <see cref="Status"/> of a token past its expiry.</summary>
    internal const string ExpiredStatus = "expired";

    /// <summary>The <see cref="Status"/> of a revoked token.</summary>
    internal const string RevokedStatus = "revoked";

    /// <summary>The list entry for <paramref name="token"/> as of <paramref name="now"/>.</summary>
    /// <param name="token">The token as the registry lists it.</param>
    /// <param name="now">The instant the request acts at.</param>
    internal static AppTokenListItem From(AppTokenView token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new(
            token.KeyId,
            token.Kind == ApiKeyKind.Service ? AppTokenResponse.ServiceKind : AppTokenResponse.PersonalKind,
            token.Name,
            token.Tenant?.Value,
            token.Roles,
            token.Scopes,
            new PersonReference(token.MintedBy.Value, token.MinterName),
            token.CreatedAt,
            token.ExpiresAt,
            token.LastUsedAt,
            token.RevokedAt,
            StatusOf(token, now));
    }

    private static string StatusOf(AppTokenView token, DateTimeOffset now) =>
        token.RevokedAt is not null ? RevokedStatus : token.ExpiresAt <= now ? ExpiredStatus : ActiveStatus;
}
