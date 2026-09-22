namespace MMLib.Alvo;

/// <summary>
/// Administering the people who can sign in and manage a project.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second contract, deliberately, rather than six more members on <see cref="IAlvoUserStore"/>.</b>
/// That port is a <em>read</em> the context resolver performs on every request, and every host that
/// registers Alvo implements it. This one is written by an administrator through a screen, and a
/// host with a read-only directory can implement the first and decline the second. Folding them
/// together would make the ordinary case carry the administrative one.
/// </para>
/// <para>
/// <b>Every member may refuse by name.</b> A host mirroring a corporate directory refuses
/// <see cref="CreateAsync"/> because accounts are not created here; a deployment on OIDC refuses
/// <see cref="IssueCredentialTokenAsync"/> because it holds no password to reset. The refusal is
/// <see cref="NotSupportedException"/> with a sentence an operator can act on, and a screen renders
/// it in place of the control. What a host may <b>not</b> do is answer a write with success and
/// change nothing.
/// </para>
/// <para>
/// <b>The caller is ambient, not a parameter.</b> Like <c>IAlvoManagement</c>, an implementation of
/// this port is reached through the core's guarded decorator, which reads the caller from
/// <c>IAlvoContextAccessor</c> — the same principal the management routes publish. An
/// implementation therefore never decides who may call it, which is what keeps authorization out
/// of a swappable adapter where it would be optional by construction.
/// </para>
/// <para>
/// <b>The bootstrap administrator is not a target of this surface.</b> Two members refuse it by
/// name — see each one — because it is the identity the whole default-deny story rests on: a
/// project whose <c>access</c> block admits nobody still has exactly one person who can fix it.
/// </para>
/// </remarks>
public interface IAlvoUserAdministration
{
    /// <summary>One page of the people on this project.</summary>
    /// <remarks>
    /// Paged from the start rather than later: the port it grew out of returns every row in no
    /// order, which is one render for a build with one account and the wrong shape at the
    /// thousands a real deployment has. Widening a port nobody implements twice yet is cheap.
    /// </remarks>
    /// <param name="query">What to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page.</returns>
    Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default);

    /// <summary>Creates a person who can sign in.</summary>
    /// <remarks>
    /// <b>No password.</b> A credential that travels as a value is readable by whoever handles it —
    /// the same reason this repository refuses a bootstrap password supplied as configuration. The
    /// new operator sets their own through <see cref="IssueCredentialTokenAsync"/>.
    /// </remarks>
    /// <param name="creation">The address, the roles and the tenant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The person that now exists.</returns>
    /// <exception cref="NotSupportedException">This host does not create accounts.</exception>
    Task<AlvoUser> CreateAsync(AlvoUserCreation creation, CancellationToken cancellationToken = default);

    /// <summary>Replaces a person's role membership.</summary>
    /// <remarks>
    /// The names are stored as given, declared or not: membership outlives a descriptor edit, and
    /// the intersection with the declared catalogue happens where the context is minted.
    /// </remarks>
    /// <param name="user">Whose roles to replace.</param>
    /// <param name="roleNames">The roles they should have.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The person as they now are.</returns>
    /// <exception cref="NotSupportedException">This host does not administer roles.</exception>
    Task<AlvoUser> SetRolesAsync(
        UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default);

    /// <summary>Grants, changes or removes the one tenant a person acts in.</summary>
    /// <param name="user">Whose tenant to set.</param>
    /// <param name="tenant">The tenant, or <see langword="null"/> to remove the grant.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The person as they now are.</returns>
    /// <exception cref="NotSupportedException">This host does not administer tenants.</exception>
    Task<AlvoUser> SetTenantAsync(
        UserId user, TenantId? tenant, CancellationToken cancellationToken = default);

    /// <summary>Bars a person from signing in, or lets them back.</summary>
    /// <remarks>
    /// <b>Refuses the bootstrap administrator by name.</b> The context resolver answers
    /// <see langword="null"/> for a disabled account <em>before</em> anything consults the bootstrap
    /// branch, and the seed does not reset an existing row — so a deployment whose <c>access</c>
    /// block admits nobody else, which is the default, would be locked out of its own management
    /// surface with no way back but editing the identity database by hand.
    /// </remarks>
    /// <param name="user">Whom to disable or restore.</param>
    /// <param name="disabled">Whether they are barred.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The person as they now are.</returns>
    /// <exception cref="NotSupportedException">This host does not disable accounts.</exception>
    Task<AlvoUser> SetDisabledAsync(
        UserId user, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>Mints a single-use token with which a person sets their own password.</summary>
    /// <remarks>
    /// <para>
    /// <b>Refuses the bootstrap administrator by name.</b> Without that, any administrator mints a
    /// token for the one account the descriptor's <c>access</c> block does not govern, sets its
    /// password and signs in as it. The bootstrap credential comes from a mounted file and rotating
    /// it is a deployment operation.
    /// </para>
    /// <para>
    /// <b>Nothing in this build delivers the token.</b> There is no mail transport, and
    /// <c>templates</c> is a warned subsystem whose reach is an after-hook on an entity write
    /// rather than an identity event. It is returned for an administrator to hand over out of band,
    /// and a screen that renders it says so.
    /// </para>
    /// </remarks>
    /// <param name="user">Who is setting a password.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The token and when it stops working.</returns>
    /// <exception cref="NotSupportedException">This host holds no credential to reset.</exception>
    Task<AlvoCredentialToken> IssueCredentialTokenAsync(
        UserId user, CancellationToken cancellationToken = default);
}

/// <summary>What to return from <see cref="IAlvoUserAdministration.ListAsync"/>.</summary>
/// <param name="Search">Matches an address, when given.</param>
/// <param name="Limit">How many rows at most.</param>
/// <param name="After">An opaque cursor from a previous page.</param>
public sealed record AlvoUserQuery(string? Search = null, int Limit = 50, string? After = null);

/// <summary>One page of people.</summary>
/// <param name="Users">The rows.</param>
/// <param name="NextCursor">Where the next page starts, when there is one.</param>
/// <param name="TotalCount">How many there are in total, when the implementation can say cheaply.</param>
public sealed record AlvoUserPage(
    IReadOnlyList<AlvoUser> Users, string? NextCursor = null, long? TotalCount = null);

/// <summary>A person to create.</summary>
/// <param name="Email">The address they sign in with.</param>
/// <param name="RoleNames">The roles they start with.</param>
/// <param name="Tenant">The tenant they act in, when they are granted one.</param>
public sealed record AlvoUserCreation(
    string Email, IReadOnlyList<string> RoleNames, TenantId? Tenant = null);

/// <summary>A single-use token with which somebody sets their own password.</summary>
/// <param name="User">Whose password it sets.</param>
/// <param name="Token">The token itself — handed over out of band.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record AlvoCredentialToken(UserId User, string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The key the unguarded implementation of <see cref="IAlvoUserAdministration"/> is registered
/// under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a key at all.</b> The guards — who may call this, nobody grants themselves a level, the
/// bootstrap administrator is not a target — live in the core, and the implementation lives in
/// whichever package holds the membership store. The core cannot name that package's type, and
/// the package must not be reachable unguarded. So the implementation registers itself under this
/// key, the core registers the guarded decorator under the plain interface, and the decorator is
/// the only thing that resolves the key.
/// </para>
/// <para>
/// There is consequently <b>no registration that hands out the raw implementation</b> to an
/// in-process consumer, which is the property a guard in a swappable adapter could never have.
/// </para>
/// </remarks>
public static class AlvoUserAdministration
{
    /// <summary>The service key the unguarded implementation registers itself under.</summary>
    public const string UnguardedKey = "alvo-user-administration-unguarded";
}
