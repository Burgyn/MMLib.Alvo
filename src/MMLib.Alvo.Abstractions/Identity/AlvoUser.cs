namespace MMLib.Alvo;

/// <summary>
/// A person who can sign in and administer a project: an internal identifier, the address they
/// sign in with, and the role <em>names</em> they have been assigned.
/// </summary>
/// <remarks>
/// <b>Names, not <see cref="Role"/> values, and that is the port's load-bearing choice.</b> A
/// <see cref="Role"/> can only be minted through a <see cref="RoleCatalog"/>, so storing one would
/// force this port to hold the descriptor's catalog and to reject — at read time — a membership row
/// that a later descriptor revision removed the role for. Membership outlives a descriptor edit;
/// the intersection with <see cref="IRoleCatalogProvider.DeclaredRoles"/> happens where the
/// <see cref="AlvoContext"/> is minted, and a name nothing declares is simply not granted.
/// </remarks>
public sealed record AlvoUser
{
    /// <summary>Gets the user's internal identifier — never the reserved all-zero value.</summary>
    public required UserId Id { get; init; }

    /// <summary>Gets the address this user signs in with.</summary>
    public required string Email { get; init; }

    /// <summary>Gets the names of the roles this user has been assigned, declared or not.</summary>
    public required IReadOnlyList<string> RoleNames { get; init; }

    /// <summary>Gets a value indicating whether this user is barred from signing in.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>
    /// Gets when a temporary lockout from failed sign-ins ends, while one stands; otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A lockout is not a disable, and the two are never both set.</b> Repeated wrong passwords bar a person
    /// for a few minutes, and anyone who knows the address can cause that; a disable is an administrator's
    /// decision and has no end. A disabled person therefore carries <see cref="IsDisabled"/> and no value here,
    /// and a person who is merely locked out is not disabled — an operator asked why somebody cannot sign in
    /// reads the one that is true (<c>docs/todo-admin.md</c> §8d item 39).
    /// </para>
    /// <para>
    /// <b>A reading, not a promise.</b> It is the lockout as the store held it when this value was read, in
    /// UTC, and it ends by itself at that instant; a value already in the past was not a lockout when read and
    /// is never projected. An implementation with no notion of a temporary lockout leaves it
    /// <see langword="null"/>. <see cref="IAlvoUserAdministration.ClearLockoutAsync"/> ends one early.
    /// </para>
    /// </remarks>
    public DateTimeOffset? LockedOutUntil { get; init; }

    /// <summary>Gets the one tenant this operator acts in, when they have been granted one.</summary>
    /// <remarks>
    /// <para>
    /// <b>One tenant, not a set, and that is a deliberate narrowing.</b> Acting in more than one
    /// tenant is a cross-tenant capability, which <c>TenantResolver</c>'s own remarks call
    /// <i>"a deliberate, audited grant"</i> and defer to #42. Until that exists, a set here would
    /// be a capability nothing in the framework can audit — so an operator holds at most one, and
    /// the resolver honours a requested tenant only as a <em>confirmation</em> of this value,
    /// never as a choice between values.
    /// </para>
    /// <para>
    /// <see langword="null"/> is the ordinary case and is not a defect: an operator with no tenant
    /// reads and writes <c>global</c> entities normally, and is refused on a tenant-scoped entity
    /// by the tenant guard — before any rule is consulted, as a <c>403</c> rather than an empty
    /// page. That refusal is the reason this value is surfaced in the dashboard rather than kept
    /// inside the resolver: an operator who cannot see that they hold no tenant has no way to
    /// understand the refusal they are looking at.
    /// </para>
    /// </remarks>
    public TenantId? Tenant { get; init; }
}
