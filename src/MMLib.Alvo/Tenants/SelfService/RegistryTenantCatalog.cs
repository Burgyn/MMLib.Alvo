using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// The tenant list of a self-service project: every tenant in the registry, personal and shared — what an
/// <see cref="AlvoScheduleScope.EachTenant"/> schedule, a tenanted schedule rule and a data migration on a scoped schema
/// run once for (spec §6.6, S3 plan D13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-service tenants only.</b> A tenant an API-key workflow writes rows under is not in the registry and is not
/// listed: a deployment that mixes API-key tenants with self-service ones registers its own
/// <see cref="IAlvoTenantCatalog"/>, which always wins over this one.
/// </para>
/// <para>
/// <b>Never registered.</b> It is resolved by <see cref="TenantCatalogs"/> only while the applied descriptor declares
/// <c>tenancy.selfService</c> and the host registers no catalog of its own, so the boot's refusal of a per-tenant
/// schedule without a tenant list keeps firing everywhere else.
/// </para>
/// </remarks>
/// <param name="store">The tenant registry.</param>
internal sealed class RegistryTenantCatalog(ITenantStore store) : IAlvoTenantCatalog
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
        store.ListTenantIdsAsync(cancellationToken);
}
