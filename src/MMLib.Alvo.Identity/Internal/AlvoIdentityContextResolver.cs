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
/// disabled user, a <see langword="null"/> role catalogue, and a caller asking to act in a tenant
/// that is not the one they hold.
/// </para>
/// <para>
/// <b>An operator does carry a tenant, and a requested one is read as a confirmation of it.</b>
/// <see cref="AlvoUser.Tenant"/> is the grant an administrator made, so the session's tenant comes
/// from the stored row and never from the request: naming the tenant you already hold is honoured,
/// naming any other is refused outright, and naming none simply leaves the grant in place. That is
/// the same rule <c>TenantResolver</c> applies to an API key — see <see cref="Confirmed"/> for why
/// the refusal is the whole principal rather than a principal with no tenant.
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
        if (!TrySubject(presentedKey, out var subject))
        {
            return null;
        }

        if (roleCatalogProvider.DeclaredRoles is not { } declared)
        {
            return null;
        }

        var user = await users.FindAsync(subject, cancellationToken).ConfigureAwait(false);
        if (user is not { IsDisabled: false } signedIn)
        {
            return null;
        }

        return Confirmed(requestedTenant, signedIn.Tenant) ? Principal(signedIn, declared) : null;
    }

    /// <summary>
    /// Decides whether a requested tenant may be honoured for this operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A requested tenant is a confirmation, never a choice</b> — the same rule
    /// <c>TenantResolver</c> applies to an API key, applied to a session. An operator holds at most
    /// one tenant (<see cref="AlvoUser.Tenant"/>), so the only honest answers to "act in tenant X"
    /// are <i>yes, that is your tenant</i> and <i>no</i>. Acting in a second one is a cross-tenant
    /// capability, which is <i>"a deliberate, audited grant"</i> deferred to #42.
    /// </para>
    /// <para>
    /// <b>The refusal is the whole principal, not a null tenant.</b> Returning a principal with no
    /// tenant would turn "you may not act in tenant X" into "you are a caller with no tenant" —
    /// which reaches every <c>global</c> entity happily, and is exactly the bool trap this
    /// resolver's contract exists to avoid.
    /// </para>
    /// <para>
    /// An unparseable requested tenant is refused rather than ignored, on the same rule: a value
    /// this resolver cannot evaluate is not a value it may drop.
    /// </para>
    /// </remarks>
    /// <param name="requested">The tenant the caller asked to act in, if any.</param>
    /// <param name="held">The tenant the operator has been granted, if any.</param>
    /// <returns><see langword="true"/> when the request may proceed.</returns>
    private static bool Confirmed(string? requested, TenantId? held)
        => string.IsNullOrEmpty(requested)
            || (TenantId.TryParse(requested, null, out var asked) && held == asked);

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
        Context = new AlvoContext
        {
            User = user.Id,
            Roles = Minted(user, declared),
            Tenant = user.Tenant,
        },
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
