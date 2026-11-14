using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// What the applied descriptor's <c>tenancy.selfService</c> says, compiled with the rest of the policy: which roles a
/// membership may carry, what a personal tenant and a tenant's creator hold, and how many shared tenants one account may
/// create.
/// </summary>
/// <remarks>
/// <b>Default-deny</b>: <see cref="None"/> — no applied descriptor, or one without <c>selfService</c> — honours no
/// membership. Roles are resolved through the declared <see cref="RoleCatalog"/>, so a name the catalog does not hold
/// is dropped rather than minted, a built-in role is never a tenant role, and a personal or creator role outside
/// <see cref="AssignableRoles"/> is dropped — each only reachable on a path that skipped <c>SelfServiceCheck</c>.
/// </remarks>
/// <param name="Enabled">Whether the descriptor declares <c>tenancy.selfService</c>.</param>
/// <param name="AssignableRoles">The tenant roles: the only roles a membership can ever carry.</param>
/// <param name="PersonalTenantRoles">The roles a person holds in their personal tenant, or <see langword="null"/> without <c>personalTenant</c>.</param>
/// <param name="CreatorRoles">The roles a shared tenant's creator receives, or <see langword="null"/> without <c>createTenants</c>.</param>
/// <param name="MaxPerAccount">How many shared tenants one account may create.</param>
internal sealed record SelfServicePolicy(
    bool Enabled,
    IReadOnlySet<Role> AssignableRoles,
    IReadOnlySet<Role>? PersonalTenantRoles,
    IReadOnlySet<Role>? CreatorRoles,
    int MaxPerAccount)
{
    /// <summary>The cap on shared tenants per account when <c>createTenants.maxPerAccount</c> is not declared.</summary>
    internal const int DefaultMaxPerAccount = 5;

    /// <summary>The policy of no self-service: no membership is honoured.</summary>
    internal static SelfServicePolicy None { get; } = new(false, new HashSet<Role>(), null, null, DefaultMaxPerAccount);

    /// <summary>Whether every person gets a tenant of their own.</summary>
    internal bool HasPersonalTenant => PersonalTenantRoles is not null;

    /// <summary>Whether a person may create shared tenants.</summary>
    internal bool CreatesTenants => CreatorRoles is not null;

    /// <summary>The stored role names that are still tenant roles under this policy — never one it does not list.</summary>
    /// <param name="stored">The role names a membership row holds.</param>
    internal IReadOnlySet<Role> Effective(IEnumerable<string> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var names = stored.ToHashSet(StringComparer.Ordinal);
        return AssignableRoles.Where(role => names.Contains(role.Name)).ToHashSet();
    }

    /// <summary>Builds the policy <paramref name="descriptor"/> declares, resolving roles through <paramref name="roles"/>.</summary>
    /// <param name="descriptor">The descriptor being compiled.</param>
    /// <param name="roles">The descriptor's declared roles.</param>
    internal static SelfServicePolicy From(AlvoDescriptor descriptor, RoleCatalog roles)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(roles);
        if (descriptor.Tenancy?.SelfService is not { } selfService)
        {
            return None;
        }

        var assignable = Resolve(selfService.AssignableRoles, roles);
        assignable.RemoveWhere(role => RoleCatalog.BuiltInOnly.TryGet(role.Name, out _));
        return new(
            true,
            assignable,
            selfService.PersonalTenant is { } personal ? Within(Resolve(personal.Roles, roles), assignable) : null,
            selfService.CreateTenants is { } create ? Within(Resolve(create.CreatorRoles, roles), assignable) : null,
            selfService.CreateTenants?.MaxPerAccount ?? DefaultMaxPerAccount);
    }

    private static HashSet<Role> Resolve(IReadOnlyList<string>? names, RoleCatalog roles) =>
        (names ?? []).Select(name => (Found: roles.TryGet(name, out var role), Role: role))
            .Where(lookup => lookup.Found)
            .Select(lookup => lookup.Role)
            .ToHashSet();

    private static HashSet<Role> Within(HashSet<Role> roles, HashSet<Role> assignable)
    {
        roles.IntersectWith(assignable);
        return roles;
    }
}
