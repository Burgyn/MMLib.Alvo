namespace MMLib.Alvo.Auth;

/// <summary>
/// A resolved caller: the <see cref="AlvoContext"/> every data operation runs as, plus the
/// API key that authenticated the request and the scopes it grants.
/// </summary>
public sealed record AlvoPrincipal
{
    /// <summary>Gets the caller's identity, roles and tenant.</summary>
    public required AlvoContext Context { get; init; }

    /// <summary>Gets the scopes the presented API key grants.</summary>
    public required IReadOnlySet<ApiKeyScope> Scopes { get; init; }

    /// <summary>Gets the identifier of the API key that authenticated this caller.</summary>
    public required string KeyId { get; init; }
}
