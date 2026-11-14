using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// A person's memberships as they count under the applied descriptor (spec §5.5's <c>memberships</c>, §6.3's
/// <c>GET /tenants</c>): only those <see cref="MembershipRules"/> would honour, each with its effective roles (S3 plan D5).
/// </summary>
/// <remarks>
/// The registry is resolved per call rather than injected: a host with no relational store registers no
/// <see cref="ITenantStore"/>, and then — as with <c>selfService</c> absent — nobody has a membership.
/// </remarks>
/// <param name="services">The container the registry is resolved from, if one is registered.</param>
/// <param name="catalogs">The applied policy catalog, whose self-service policy decides.</param>
internal sealed class SelfServiceMemberships(IServiceProvider services, IPolicyCatalogProvider catalogs)
{
    /// <summary>The memberships of <paramref name="user"/> the applied descriptor honours: the personal one first.</summary>
    /// <param name="user">The person.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal async Task<IReadOnlyList<MembershipResponse>> ListAsync(UserId user, CancellationToken ct)
    {
        var policy = catalogs.Current?.SelfService ?? SelfServicePolicy.None;
        return !policy.Enabled || services.GetService<ITenantStore>() is not { } store
            ? []
            : await ListAsync(store, policy, user, ct).ConfigureAwait(false);
    }

    /// <summary>The memberships of <paramref name="user"/> <paramref name="policy"/> honours, read from <paramref name="store"/>.</summary>
    /// <param name="store">The registry.</param>
    /// <param name="policy">The applied self-service policy, enabled.</param>
    /// <param name="user">The person.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    internal static async Task<IReadOnlyList<MembershipResponse>> ListAsync(
        ITenantStore store, SelfServicePolicy policy, UserId user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        var stored = await store.ListMembershipsAsync(user, ct).ConfigureAwait(false);
        return [.. stored.Where(membership => Honoured(membership, policy)).Select(membership => Response(membership, policy))];
    }

    /// <summary>Whether <paramref name="policy"/> honours <paramref name="membership"/>: a personal one only with personal tenants.</summary>
    /// <param name="membership">The stored membership.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static bool Honoured(StoredMembership membership, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Enabled && (membership.Kind == TenantKind.Shared || policy.HasPersonalTenant);
    }

    /// <summary>The response for <paramref name="membership"/> with the roles <paramref name="policy"/> makes effective (D5).</summary>
    /// <param name="membership">The stored membership.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static MembershipResponse Response(StoredMembership membership, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(policy);
        return MembershipResponse.From(membership, Roles(membership.Kind, membership.Roles, policy));
    }

    /// <summary>
    /// The tenant roles a membership of <paramref name="kind"/> storing <paramref name="stored"/> carries under
    /// <paramref name="policy"/>: a personal one's are <c>personalTenant.roles</c>, a shared one's the stored names still
    /// assignable — never a name the descriptor no longer lists.
    /// </summary>
    /// <param name="kind">The tenant's kind.</param>
    /// <param name="stored">The stored role names.</param>
    /// <param name="policy">The applied self-service policy.</param>
    internal static IReadOnlySet<Role> Roles(TenantKind kind, IEnumerable<string> stored, SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return kind == TenantKind.Personal
            ? policy.PersonalTenantRoles ?? new HashSet<Role>()
            : policy.Effective(stored);
    }
}
