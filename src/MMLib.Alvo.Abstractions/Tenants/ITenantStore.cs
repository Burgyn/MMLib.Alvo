namespace MMLib.Alvo.Tenants;

/// <summary>
/// The self-service tenant registry — tenants and the people who are members of them (accounts spec §6.1). Every write
/// is one store call, one transaction. Implemented by the relational drivers; the core and <c>MMLib.Alvo.Accounts</c>
/// reach it through <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, the <c>IAccountStore</c> precedent</b> (S3 plan D2): in-family producers and consumers only, so publishing
/// it would be a commitment no third party has asked for. Making it public later is additive.
/// </para>
/// <para>
/// <b>The registry is the source of truth for people, not for tenant ids</b> (spec §6.6): a tenant an API-key workflow
/// writes rows under is not in it and need not be.
/// </para>
/// <para>
/// <b>Every instant and every id is passed in</b>, as on <c>IAccountStore</c>: the store reads no clock and mints no id.
/// </para>
/// </remarks>
internal interface ITenantStore
{
    /// <summary>
    /// Creates a shared tenant with its creator as its owner, in one transaction (S3 plan D3, D9): the creator's account
    /// row is locked first, so concurrent creates of one person are serialised and none passes
    /// <see cref="CreateTenantRequest.MaxPerAccount"/>. A refusal writes nothing.
    /// </summary>
    /// <param name="request">The tenant's id and name, its creator and their roles, the cap and the instant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct);

    /// <summary>The memberships of <paramref name="user"/>: the personal one first, then by joining, then by tenant id.</summary>
    /// <param name="user">The account whose memberships are listed.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct);

    /// <summary>
    /// The membership of <paramref name="user"/> in <paramref name="tenant"/> — or, when <paramref name="tenant"/> is
    /// <see langword="null"/>, in their personal tenant — or <see langword="null"/> when they are no member of it.
    /// </summary>
    /// <param name="user">The account.</param>
    /// <param name="tenant">The tenant, or <see langword="null"/> for the personal one.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct);

    /// <summary>
    /// Every tenant in the registry with its kind, by creation and then id — the self-service tenants only (spec §6.6). The
    /// kind is the caller's to judge: which tenants count under the applied descriptor is the core's decision, not the store's.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct);

    /// <summary>Renames <paramref name="tenant"/> — its owner's act, a personal tenant's owner included.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="actor">The account renaming it.</param>
    /// <param name="name">The new name.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <see cref="MemberChangeStatus.Changed"/>, <see cref="MemberChangeStatus.NotFound"/> (no such tenant, or the actor is no
    /// member of it) or <see cref="MemberChangeStatus.NotOwner"/>.
    /// </returns>
    Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct);

    /// <summary>
    /// The members of <paramref name="tenant"/> with their accounts' display names and avatars, by joining and then account
    /// id — or <see langword="null"/> when <paramref name="actor"/> is no member of it, so a stranger learns nothing.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="actor">The account asking.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct);

    /// <summary>
    /// Changes another member's roles and/or ownership — an owner's act, in one transaction that first bumps the tenant's
    /// <c>version</c>, so two owners of one tenant are serialised (D3). Nobody changes their own membership (spec §14 AC4),
    /// a personal tenant's membership never changes, and the last owner never loses ownership. Roles are stored verbatim;
    /// whether they are assignable is the core's check.
    /// </summary>
    /// <param name="update">The tenant, the actor, the member, and what changes.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct);

    /// <summary>
    /// Removes a member — an owner's act — or lets a member leave, in one transaction that first bumps the tenant's
    /// <c>version</c> (D3). A personal tenant's owner never leaves it, and the last owner never leaves or is removed
    /// (spec §6.4).
    /// </summary>
    /// <param name="removal">The tenant, the actor and the member.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct);
}

/// <summary>What kind of tenant a registry tenant is.</summary>
internal enum TenantKind
{
    /// <summary>A person's own tenant: exactly one member, its owner, forever (spec §6.1).</summary>
    Personal,

    /// <summary>A tenant a person created to share with others.</summary>
    Shared,
}

/// <summary>One tenant, as the registry lists it.</summary>
/// <param name="Id">The tenant.</param>
/// <param name="Kind">The tenant's kind.</param>
internal sealed record StoredTenant(TenantId Id, TenantKind Kind);

/// <summary>One membership, as the registry stores it.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Kind">The tenant's kind.</param>
/// <param name="Name">The tenant's name.</param>
/// <param name="Roles">The stored tenant-role names, verbatim — the core intersects them with the descriptor (D5).</param>
/// <param name="IsOwner">Whether the member manages the tenant; never a role (spec §6.2).</param>
/// <param name="JoinedAt">When the membership began.</param>
internal sealed record StoredMembership(
    TenantId Tenant, TenantKind Kind, string Name, IReadOnlyList<string> Roles, bool IsOwner, DateTimeOffset JoinedAt);

/// <summary>One shared-tenant creation, with every id and instant chosen by the caller.</summary>
/// <param name="Id">The new tenant's id.</param>
/// <param name="Name">The new tenant's name.</param>
/// <param name="Creator">The account creating it, which becomes its owner.</param>
/// <param name="CreatorRoles">The tenant roles the creator's membership starts with (<c>createTenants.creatorRoles</c>).</param>
/// <param name="MaxPerAccount">How many shared tenants one account may create (<c>createTenants.maxPerAccount</c>).</param>
/// <param name="Now">The instant of the creation.</param>
internal sealed record CreateTenantRequest(
    TenantId Id, string Name, UserId Creator, IReadOnlyList<string> CreatorRoles, int MaxPerAccount, DateTimeOffset Now);

/// <summary>What a shared-tenant creation did.</summary>
internal enum CreateTenantStatus
{
    /// <summary>The tenant and its owner membership were created.</summary>
    Created,

    /// <summary>The creator has already created <see cref="CreateTenantRequest.MaxPerAccount"/> shared tenants; nothing was written.</summary>
    LimitReached,

    /// <summary>The creator has no enabled account; nothing was written.</summary>
    NoAccount,
}

/// <summary>A creation's status and, when it created the tenant, the creator's membership.</summary>
/// <param name="Status">What the creation did.</param>
/// <param name="Membership">The creator's owner membership, for <see cref="CreateTenantStatus.Created"/>.</param>
internal readonly record struct CreateTenantOutcome(CreateTenantStatus Status, StoredMembership? Membership);

/// <summary>The personal tenant a sign-in or a refresh ensures for its account (S3 plan D6).</summary>
/// <param name="Id">The id the personal tenant gets if it does not exist yet.</param>
internal sealed record PersonalTenantRequest(TenantId Id);

/// <summary>One member of a tenant, as the members list shows it (spec §6.3).</summary>
/// <param name="User">The member's account.</param>
/// <param name="DisplayName">The account's display name, if any — <see langword="null"/> too when the account is gone.</param>
/// <param name="AvatarUrl">The account's avatar URL, if any.</param>
/// <param name="Roles">The stored tenant-role names, verbatim.</param>
/// <param name="IsOwner">Whether the member manages the tenant.</param>
/// <param name="JoinedAt">When the membership began.</param>
internal sealed record MemberView(
    UserId User, string? DisplayName, string? AvatarUrl, IReadOnlyList<string> Roles, bool IsOwner, DateTimeOffset JoinedAt);

/// <summary>A change to another member's membership; a <see langword="null"/> part is left as it is.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Actor">The account making the change.</param>
/// <param name="Member">The member being changed.</param>
/// <param name="Roles">The member's new roles, or <see langword="null"/> to keep them.</param>
/// <param name="IsOwner">The member's new ownership, or <see langword="null"/> to keep it.</param>
/// <param name="Now">The instant of the change.</param>
internal sealed record MemberUpdate(
    TenantId Tenant, UserId Actor, UserId Member, IReadOnlyList<string>? Roles, bool? IsOwner, DateTimeOffset Now);

/// <summary>A member's removal by an owner, or a member leaving (<paramref name="Actor"/> is <paramref name="Member"/>).</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Actor">The account removing.</param>
/// <param name="Member">The member removed.</param>
/// <param name="Now">The instant of the removal.</param>
internal sealed record MemberRemoval(TenantId Tenant, UserId Actor, UserId Member, DateTimeOffset Now);

/// <summary>What a tenant or membership write did; every status but <see cref="Changed"/> wrote nothing.</summary>
internal enum MemberChangeStatus
{
    /// <summary>The change was written.</summary>
    Changed,

    /// <summary>No such tenant, or the actor is no member of it — the two are indistinguishable on purpose.</summary>
    NotFound,

    /// <summary>The actor is a member but not an owner, and the change is an owner's.</summary>
    NotOwner,

    /// <summary>The member to change or remove is no member of the tenant.</summary>
    NoSuchMember,

    /// <summary>The change would leave the tenant without an owner (spec §6.4).</summary>
    LastOwner,

    /// <summary>The tenant is a personal one, whose only membership never changes (spec §6.1).</summary>
    PersonalTenant,

    /// <summary>The actor aimed a membership change at themself (spec §14 AC4); leaving is a removal.</summary>
    Self,
}
