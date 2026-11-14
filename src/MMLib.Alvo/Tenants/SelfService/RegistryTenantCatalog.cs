using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The tenant list of a self-service project: the registry's tenants the applied descriptor honours — every shared tenant,
/// and the personal ones only while <c>personalTenant</c> is on — what an <see cref="AlvoScheduleScope.EachTenant"/>
/// schedule, a tenanted schedule rule and a data migration on a scoped schema run once for (spec §6.6, S3 plan D13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only tenants someone can act in.</b> Which tenants count is <see cref="MembershipRules.Honours(TenantKind, SelfServicePolicy)"/>,
/// the same test every membership passes: after <c>personalTenant</c> is dropped no request can act in a personal tenant, so
/// no run is minted for one either.
/// </para>
/// <para>
/// <b>Self-service tenants only.</b> A tenant an API-key workflow writes rows under is not in the registry and is not
/// listed: a deployment that mixes API-key tenants with self-service ones registers its own
/// <see cref="IAlvoTenantCatalog"/>, which always wins over this one.
/// </para>
/// <para>
/// <b>Grows with the sign-ups.</b> Under an open sign-up with <c>personalTenant</c> every account adds a tenant, and so a
/// run to every per-tenant schedule tick and scoped data migration — a cost, not a wall breach (each run is the system in
/// its own tenant). A host that wants it bounded registers its own catalog.
/// </para>
/// <para>
/// <b>Never registered.</b> It is resolved by <see cref="TenantCatalogs"/> only while the descriptor judged declares
/// <c>tenancy.selfService</c> and the host registers no catalog of its own, so the boot's refusal of a per-tenant
/// schedule without a tenant list keeps firing everywhere else.
/// </para>
/// </remarks>
/// <param name="store">The tenant registry.</param>
/// <param name="policy">The self-service policy of the descriptor the listing is judged by.</param>
internal sealed class RegistryTenantCatalog(ITenantStore store, SelfServicePolicy policy) : IAlvoTenantCatalog
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await store.ListTenantsAsync(cancellationToken).ConfigureAwait(false);
        return [.. tenants.Where(tenant => MembershipRules.Honours(tenant.Kind, policy)).Select(tenant => tenant.Id)];
    }
}
