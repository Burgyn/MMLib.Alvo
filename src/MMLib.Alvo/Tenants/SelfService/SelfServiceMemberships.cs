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
        if (!policy.Enabled || services.GetService<ITenantStore>() is not { } store)
        {
            return [];
        }

        var stored = await store.ListMembershipsAsync(user, ct).ConfigureAwait(false);
        return [.. stored.Where(membership => membership.Kind == TenantKind.Shared || policy.HasPersonalTenant)
            .Select(membership => MembershipResponse.From(membership, Roles(membership, policy)))];
    }

    private static IReadOnlySet<Role> Roles(StoredMembership membership, SelfServicePolicy policy) =>
        membership.Kind == TenantKind.Personal ? policy.PersonalTenantRoles! : policy.Effective(membership.Roles);
}
