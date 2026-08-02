using System.Text;

namespace MMLib.Alvo.Auth;

/// <summary>The stored record of an issued API key, as persisted by an <see cref="IApiKeyStore"/>.</summary>
public sealed record ApiKeyRecord
{
    /// <summary>Gets the key's public identifier.</summary>
    public required string KeyId { get; init; }

    /// <summary>Gets the base64-encoded SHA-256 hash of the secret; the secret itself is never stored.</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>Gets the user this key authenticates as.</summary>
    public required UserId User { get; init; }

    /// <summary>Gets the names of the roles this key grants.</summary>
    public required IReadOnlyList<string> RoleNames { get; init; }

    /// <summary>Gets the tenant this key is scoped to, if any.</summary>
    public TenantId? Tenant { get; init; }

    /// <summary>Gets the entity/access scopes this key grants.</summary>
    public required IReadOnlySet<ApiKeyScope> Scopes { get; init; }

    /// <summary>Gets when this key expires, if ever.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Gets when this key was revoked, if it was.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Gets when this key was last used, if ever.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Answers whether this key is usable at <paramref name="now"/>: not revoked and not expired.</summary>
    /// <param name="now">The instant to evaluate usability at.</param>
    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("KeyId = ").Append(KeyId);
        builder.Append(", Sha256Hash = ***");
        builder.Append(", User = ").Append(User);
        builder.Append(", RoleNames = [").Append(string.Join(", ", RoleNames)).Append(']');
        builder.Append(", Tenant = ").Append(Tenant);
        builder.Append(", Scopes = ").Append(Scopes.Count).Append(" scope(s)");
        builder.Append(", ExpiresAt = ").Append(ExpiresAt);
        builder.Append(", RevokedAt = ").Append(RevokedAt);
        builder.Append(", LastUsedAt = ").Append(LastUsedAt);
        return true;
    }
}
