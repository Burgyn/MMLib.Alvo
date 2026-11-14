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

    /// <summary>Every tenant in the registry, by creation and then id — the self-service tenants only (spec §6.6).</summary>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<IReadOnlyList<TenantId>> ListTenantIdsAsync(CancellationToken ct);
}

/// <summary>What kind of tenant a registry tenant is.</summary>
internal enum TenantKind
{
    /// <summary>A person's own tenant: exactly one member, its owner, forever (spec §6.1).</summary>
    Personal,

    /// <summary>A tenant a person created to share with others.</summary>
    Shared,
}

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
