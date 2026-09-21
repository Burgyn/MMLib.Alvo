using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Identity.Internal;

/// <summary>
/// Mints the <see cref="AlvoContext"/> a signed-in person acts as: the cookie's
/// already-authenticated subject, plus the roles they are a member of <b>intersected</b> with the
/// roles the applied descriptor declares.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>presentedKey</c> is a subject ASP.NET Core has already authenticated, never a
/// credential.</b> The port is deliberately ASP.NET-free — it takes a string, never an
/// <c>HttpContext</c> — and the cookie middleware is what turns a signed cookie into that string.
/// This instance is therefore registered under <see cref="AlvoIdentity.ResolverKey"/> and is
/// <em>not</em> the unkeyed resolver the Data API hands the raw <c>X-Alvo-Api-Key</c> header to:
/// reaching it from there would make "knows an operator's uuid" a working credential.
/// </para>
/// <para>
/// <b>Every refusal is <see langword="null"/>, and there are five of them:</b> a subject that is not
/// a usable <see cref="UserId"/> (including the reserved all-zero value), an unknown user, a
/// disabled user, a <see langword="null"/> role catalogue, and a caller asking to act in a tenant.
/// The last is not an oversight — a cookie session carries no tenant grant, so honouring the request
/// would let the caller choose the tenant it acts in, which is the one thing <c>TenantResolver</c>
/// exists to refuse for an API key.
/// </para>
/// </remarks>
/// <param name="users">The membership store.</param>
/// <param name="roleCatalogProvider">The roles the applied descriptor declares.</param>
internal sealed class AlvoIdentityContextResolver(
    IAlvoUserStore users,
    IRoleCatalogProvider roleCatalogProvider) : IAlvoContextResolver
{
    /// <summary>
    /// What a session grants before the descriptor's rules run.
    /// </summary>
    /// <remarks>
    /// Scopes exist to <em>narrow</em> an API key below what its roles would otherwise reach. A
    /// person's authority is the descriptor's rules, so the wildcard pair is the honest value: an
    /// empty scope set would deny every operation before any rule was consulted, which would look
    /// like a policy decision and be a plumbing one.
    /// </remarks>
    private static readonly IReadOnlySet<ApiKeyScope> _sessionScopes = new HashSet<ApiKeyScope>
    {
        new() { Entity = "*", Access = ScopeAccess.Read },
        new() { Entity = "*", Access = ScopeAccess.Write },
    };

    /// <inheritdoc/>
    public async ValueTask<AlvoPrincipal?> ResolveAsync(
        string? presentedKey, string? requestedTenant, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(requestedTenant) || !TrySubject(presentedKey, out var subject))
        {
            return null;
        }

        if (roleCatalogProvider.DeclaredRoles is not { } declared)
        {
            return null;
        }

        var user = await users.FindAsync(subject, cancellationToken).ConfigureAwait(false);
        return user is { IsDisabled: false } signedIn ? Principal(signedIn, declared) : null;
    }

    /// <summary>Reads the cookie subject as a usable <see cref="UserId"/>.</summary>
    /// <param name="presented">The already-authenticated subject.</param>
    /// <param name="subject">The parsed identifier, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the subject is a non-reserved identifier.</returns>
    private static bool TrySubject(string? presented, out UserId subject)
        => UserId.TryParse(presented, null, out subject) && subject != default;

    /// <summary>Assembles the caller from a stored user and the declared catalogue.</summary>
    /// <param name="user">The stored user.</param>
    /// <param name="declared">The roles the applied descriptor declares.</param>
    /// <returns>The resolved caller.</returns>
    private static AlvoPrincipal Principal(AlvoUser user, RoleCatalog declared) => new()
    {
        Context = new AlvoContext { User = user.Id, Roles = Minted(user, declared) },
        Scopes = _sessionScopes,
        KeyId = SessionKeyId(user.Id),
    };

    /// <summary>
    /// The roles this caller actually holds: <see cref="Role.Authenticated"/>, plus every assigned
    /// name the applied descriptor still declares.
    /// </summary>
    /// <remarks>
    /// A name it does not declare is dropped silently rather than refused — an identity source's role
    /// vocabulary is larger than one backend's, and only the overlap means anything to Alvo's rules.
    /// </remarks>
    /// <param name="user">The stored user.</param>
    /// <param name="declared">The roles the applied descriptor declares.</param>
    /// <returns>The roles to mint.</returns>
    private static HashSet<Role> Minted(AlvoUser user, RoleCatalog declared) =>
        user.RoleNames
            .Where(name => declared.TryGet(name, out _))
            .Select(declared.Get)
            .Append(Role.Authenticated)
            .ToHashSet();

    /// <summary>The key identifier a session reports, so an audit line can tell one from an API key.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <returns>The identifier.</returns>
    private static string SessionKeyId(UserId user) => $"session:{user}";
}
