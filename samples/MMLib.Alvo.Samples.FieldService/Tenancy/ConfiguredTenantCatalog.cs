using Microsoft.Extensions.Options;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Samples.FieldService.Tenancy;

/// <summary>The tenants this host serves, as its configuration lists them.</summary>
/// <remarks>
/// <b>Why C#:</b> Alvo does not own a tenant directory — a descriptor can scope rows to a tenant but cannot enumerate the
/// tenants that exist. A per-tenant schedule (the overdue report) needs that list, so the host supplies it. A SaaS host
/// would read its own tenant table here; this sample reads <see cref="FieldServiceOptions.Tenants"/>.
/// </remarks>
internal sealed class ConfiguredTenantCatalog(IOptions<FieldServiceOptions> options) : IAlvoTenantCatalog
{
    public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantId>>([.. options.Value.Tenants.Select(tenant => new TenantId(tenant))]);
}
