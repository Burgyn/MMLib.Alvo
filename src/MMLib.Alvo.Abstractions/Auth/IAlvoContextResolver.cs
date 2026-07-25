namespace MMLib.Alvo.Auth;

/// <summary>
/// Resolves the presented credential into an <see cref="AlvoPrincipal"/>. ASP.NET-free by
/// design: it takes the presented key, never an <c>HttpContext</c>, so it works identically
/// in standalone and embedded mode.
/// </summary>
public interface IAlvoContextResolver
{
    /// <summary>
    /// Resolves a presented credential. Returns <see langword="null"/> — deny, never a
    /// partially-trusted principal — for a credential that is absent, malformed, expired,
    /// revoked, or for a mismatched requested tenant.
    /// </summary>
    /// <param name="presentedKey">The raw credential presented by the caller, if any.</param>
    /// <param name="requestedTenant">The tenant the caller asked to act in, if any.</param>
    /// <param name="cancellationToken">A token to cancel resolution.</param>
    ValueTask<AlvoPrincipal?> ResolveAsync(
        string? presentedKey,
        string? requestedTenant,
        CancellationToken cancellationToken);
}
